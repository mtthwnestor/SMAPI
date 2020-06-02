using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
#if SMAPI_FOR_WINDOWS
using System.Windows.Forms;
#endif
using Newtonsoft.Json;
using StardewModdingAPI.Events;
using StardewModdingAPI.Framework.Events;
using StardewModdingAPI.Framework.Exceptions;
using StardewModdingAPI.Framework.Logging;
using StardewModdingAPI.Framework.Models;
using StardewModdingAPI.Framework.ModHelpers;
using StardewModdingAPI.Framework.ModLoading;
using StardewModdingAPI.Framework.Patching;
using StardewModdingAPI.Framework.PerformanceMonitoring;
using StardewModdingAPI.Framework.Reflection;
using StardewModdingAPI.Framework.Serialization;
using StardewModdingAPI.Patches;
using StardewModdingAPI.Toolkit;
using StardewModdingAPI.Toolkit.Framework.Clients.WebApi;
using StardewModdingAPI.Toolkit.Framework.ModData;
using StardewModdingAPI.Toolkit.Serialization;
using StardewModdingAPI.Toolkit.Utilities;
using StardewModdingAPI.Utilities;
using StardewValley;
using Object = StardewValley.Object;
using ThreadState = System.Threading.ThreadState;

namespace StardewModdingAPI.Framework
{
    /// <summary>The core class which initializes and manages SMAPI.</summary>
    internal class SCore : IDisposable
    {
        /*********
        ** Fields
        *********/
        /// <summary>The log file to which to write messages.</summary>
        private readonly LogFileManager LogFile;

        /// <summary>Manages console output interception.</summary>
        private readonly ConsoleInterceptionManager ConsoleManager = new ConsoleInterceptionManager();

        /// <summary>The core logger and monitor for SMAPI.</summary>
        private readonly Monitor Monitor;

        /// <summary>The core logger and monitor on behalf of the game.</summary>
        private readonly Monitor MonitorForGame;

        /// <summary>Tracks whether the game should exit immediately and any pending initialization should be cancelled.</summary>
        private readonly CancellationTokenSource CancellationToken = new CancellationTokenSource();

        /// <summary>Simplifies access to private game code.</summary>
        private readonly Reflector Reflection = new Reflector();

        /// <summary>Encapsulates access to SMAPI core translations.</summary>
        private readonly Translator Translator = new Translator();

        /// <summary>The SMAPI configuration settings.</summary>
        private readonly SConfig Settings;

        /// <summary>The underlying game instance.</summary>
        private SGame GameInstance;

        /// <summary>The underlying content manager.</summary>
        private ContentCoordinator ContentCore => this.GameInstance.ContentCore;

        /// <summary>Tracks the installed mods.</summary>
        /// <remarks>This is initialized after the game starts.</remarks>
        private readonly ModRegistry ModRegistry = new ModRegistry();

        /// <summary>Manages SMAPI events for mods.</summary>
        private readonly EventManager EventManager;

        /// <summary>Whether the game is currently running.</summary>
        private bool IsGameRunning;

        /// <summary>Whether the program has been disposed.</summary>
        private bool IsDisposed;

        /// <summary>Regex patterns which match console non-error messages to suppress from the console and log.</summary>
        private readonly Regex[] SuppressConsolePatterns =
        {
            new Regex(@"^TextBox\.Selected is now '(?:True|False)'\.$", RegexOptions.Compiled | RegexOptions.CultureInvariant),
            new Regex(@"^(?:FRUIT )?TREE: IsClient:(?:True|False) randomOutput: \d+$", RegexOptions.Compiled | RegexOptions.CultureInvariant),
            new Regex(@"^loadPreferences\(\); begin", RegexOptions.Compiled | RegexOptions.CultureInvariant),
            new Regex(@"^savePreferences\(\); async=", RegexOptions.Compiled | RegexOptions.CultureInvariant),
            new Regex(@"^DebugOutput:\s+(?:added CLOUD|added cricket|dismount tile|Ping|playerPos)", RegexOptions.Compiled | RegexOptions.CultureInvariant)
        };

        /// <summary>Regex patterns which match console messages to show a more friendly error for.</summary>
        private readonly ReplaceLogPattern[] ReplaceConsolePatterns =
        {
            // Steam not loaded
            new ReplaceLogPattern(
                search: new Regex(@"^System\.InvalidOperationException: Steamworks is not initialized\.[\s\S]+$", RegexOptions.Compiled | RegexOptions.CultureInvariant),
                replacement:
#if SMAPI_FOR_WINDOWS
                    "Oops! Steam achievements won't work because Steam isn't loaded. You can launch the game through Steam to fix that (see 'Part 2: Configure Steam' in the install guide for more info: https://smapi.io/install).",
#else
                    "Oops! Steam achievements won't work because Steam isn't loaded. You can launch the game through Steam to fix that.",
#endif
                logLevel: LogLevel.Error
            ),

            // save file not found error
            new ReplaceLogPattern(
                search: new Regex(@"^System\.IO\.FileNotFoundException: [^\n]+\n[^:]+: '[^\n]+[/\\]Saves[/\\]([^'\r\n]+)[/\\]([^'\r\n]+)'[\s\S]+LoadGameMenu\.FindSaveGames[\s\S]+$", RegexOptions.Compiled | RegexOptions.CultureInvariant),
                replacement: "The game can't find the '$2' file for your '$1' save. See https://stardewvalleywiki.com/Saves#Troubleshooting for help.",
                logLevel: LogLevel.Error
            )
        };

        /// <summary>The mod toolkit used for generic mod interactions.</summary>
        private readonly ModToolkit Toolkit = new ModToolkit();

        /// <summary>The path to search for mods.</summary>
        private string ModsPath => Constants.ModsPath;


        /*********
        ** Accessors
        *********/
        /// <summary>Manages deprecation warnings.</summary>
        /// <remarks>This is initialized after the game starts. This is accessed directly because it's not part of the normal class model.</remarks>
        internal static DeprecationManager DeprecationManager { get; private set; }

        /// <summary>Manages performance counters.</summary>
        /// <remarks>This is initialized after the game starts. This is non-private for use by Console Commands.</remarks>
        internal static PerformanceMonitor PerformanceMonitor { get; private set; }


        /*********
        ** Public methods
        *********/
        /// <summary>Construct an instance.</summary>
        /// <param name="modsPath">The path to search for mods.</param>
        /// <param name="writeToConsole">Whether to output log messages to the console.</param>
        public SCore(string modsPath, bool writeToConsole)
        {
            // init paths
            this.VerifyPath(modsPath);
            this.VerifyPath(Constants.LogDir);
            Constants.ModsPath = modsPath;

            // init log file
            this.PurgeNormalLogs();
            string logPath = this.GetLogPath();

            // init basics
            this.Settings = JsonConvert.DeserializeObject<SConfig>(File.ReadAllText(Constants.ApiConfigPath));
            if (File.Exists(Constants.ApiUserConfigPath))
                JsonConvert.PopulateObject(File.ReadAllText(Constants.ApiUserConfigPath), this.Settings);

            this.LogFile = new LogFileManager(logPath);
            this.Monitor = new Monitor("SMAPI", this.ConsoleManager, this.LogFile, this.Settings.ConsoleColors, this.Settings.VerboseLogging)
            {
                WriteToConsole = writeToConsole,
                ShowTraceInConsole = this.Settings.DeveloperMode,
                ShowFullStampInConsole = this.Settings.DeveloperMode
            };
            this.MonitorForGame = this.GetSecondaryMonitor("game");

            SCore.PerformanceMonitor = new PerformanceMonitor(this.Monitor);
            this.EventManager = new EventManager(this.Monitor, this.ModRegistry, SCore.PerformanceMonitor);
            SCore.PerformanceMonitor.InitializePerformanceCounterCollections(this.EventManager);

            SCore.DeprecationManager = new DeprecationManager(this.Monitor, this.ModRegistry);

            SDate.Translations = this.Translator;

            // redirect direct console output
            if (this.MonitorForGame.WriteToConsole)
                this.ConsoleManager.OnMessageIntercepted += message => this.HandleConsoleMessage(this.MonitorForGame, message);

            // init logging
            this.Monitor.Log($"SMAPI {Constants.ApiVersion} with Stardew Valley {Constants.GameVersion} on {EnvironmentUtility.GetFriendlyPlatformName(Constants.Platform)}", LogLevel.Info);
            this.Monitor.Log($"Mods go here: {modsPath}", LogLevel.Info);
            if (modsPath != Constants.DefaultModsPath)
                this.Monitor.Log("(Using custom --mods-path argument.)", LogLevel.Trace);
            this.Monitor.Log($"Log started at {DateTime.UtcNow:s} UTC", LogLevel.Trace);

            // log custom settings
            {
                IDictionary<string, object> customSettings = this.Settings.GetCustomSettings();
                if (customSettings.Any())
                    this.Monitor.Log($"Loaded with custom settings: {string.Join(", ", customSettings.OrderBy(p => p.Key).Select(p => $"{p.Key}: {p.Value}"))}", LogLevel.Trace);
            }

            // validate platform
#if SMAPI_FOR_WINDOWS
            if (Constants.Platform != Platform.Windows)
            {
                this.Monitor.Log("Oops! You're running Windows, but this version of SMAPI is for Linux or Mac. Please reinstall SMAPI to fix this.", LogLevel.Error);
                this.PressAnyKeyToExit();
                return;
            }
#else
            if (Constants.Platform == Platform.Windows)
            {
                this.Monitor.Log($"Oops! You're running {Constants.Platform}, but this version of SMAPI is for Windows. Please reinstall SMAPI to fix this.", LogLevel.Error);
                this.PressAnyKeyToExit();
                return;
            }
#endif
        }

        /// <summary>Launch SMAPI.</summary>
        [HandleProcessCorruptedStateExceptions, SecurityCritical] // let try..catch handle corrupted state exceptions
        public void RunInteractively()
        {
            // initialize SMAPI
            try
            {
                JsonConverter[] converters = {
                    new ColorConverter(),
                    new PointConverter(),
                    new Vector2Converter(),
                    new RectangleConverter()
                };
                foreach (JsonConverter converter in converters)
                    this.Toolkit.JsonHelper.JsonSettings.Converters.Add(converter);

                // add error handlers
#if SMAPI_FOR_WINDOWS
                Application.ThreadException += (sender, e) => this.Monitor.Log($"Critical thread exception: {e.Exception.GetLogSummary()}", LogLevel.Error);
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
#endif
                AppDomain.CurrentDomain.UnhandledException += (sender, e) => this.Monitor.Log($"Critical app domain exception: {e.ExceptionObject}", LogLevel.Error);

                // add more lenient assembly resolver
                AppDomain.CurrentDomain.AssemblyResolve += (sender, e) => AssemblyLoader.ResolveAssembly(e.Name);

                // hook locale event
                LocalizedContentManager.OnLanguageChange += locale => this.OnLocaleChanged();

                // override game
                SGame.ConstructorHack = new SGameConstructorHack(this.Monitor, this.Reflection, this.Toolkit.JsonHelper, this.InitializeBeforeFirstAssetLoaded);
                this.GameInstance = new SGame(
                    monitor: this.Monitor,
                    monitorForGame: this.MonitorForGame,
                    reflection: this.Reflection,
                    translator: this.Translator,
                    eventManager: this.EventManager,
                    jsonHelper: this.Toolkit.JsonHelper,
                    modRegistry: this.ModRegistry,
                    deprecationManager: SCore.DeprecationManager,
                    performanceMonitor: SCore.PerformanceMonitor,
                    onGameInitialized: this.InitializeAfterGameStart,
                    onGameExiting: this.Dispose,
                    cancellationToken: this.CancellationToken,
                    logNetworkTraffic: this.Settings.LogNetworkTraffic
                );
                this.Translator.SetLocale(this.GameInstance.ContentCore.GetLocale(), this.GameInstance.ContentCore.Language);
                StardewValley.Program.gamePtr = this.GameInstance;

                // apply game patches
                new GamePatcher(this.Monitor).Apply(
                    new EventErrorPatch(this.MonitorForGame),
                    new DialogueErrorPatch(this.MonitorForGame, this.Reflection),
                    new ObjectErrorPatch(),
                    new LoadContextPatch(this.Reflection, this.GameInstance.OnLoadStageChanged),
                    new LoadErrorPatch(this.Monitor, this.GameInstance.OnSaveContentRemoved),
                    new ScheduleErrorPatch(this.MonitorForGame)
                );

                // add exit handler
                new Thread(() =>
                {
                    this.CancellationToken.Token.WaitHandle.WaitOne();
                    if (this.IsGameRunning)
                    {
                        try
                        {
                            File.WriteAllText(Constants.FatalCrashMarker, string.Empty);
                            File.Copy(this.LogFile.Path, Constants.FatalCrashLog, overwrite: true);
                        }
                        catch (Exception ex)
                        {
                            this.Monitor.Log($"SMAPI failed trying to track the crash details: {ex.GetLogSummary()}", LogLevel.Error);
                        }

                        this.GameInstance.Exit();
                    }
                }).Start();

                // set window titles
                this.GameInstance.Window.Title = $"Stardew Valley {Constants.GameVersion} - running SMAPI {Constants.ApiVersion}";
                Console.Title = $"SMAPI {Constants.ApiVersion} - running Stardew Valley {Constants.GameVersion}";
            }
            catch (Exception ex)
            {
                this.Monitor.Log($"SMAPI failed to initialize: {ex.GetLogSummary()}", LogLevel.Error);
                this.PressAnyKeyToExit();
                return;
            }

            // check update marker
            if (File.Exists(Constants.UpdateMarker))
            {
                string rawUpdateFound = File.ReadAllText(Constants.UpdateMarker);
                if (SemanticVersion.TryParse(rawUpdateFound, out ISemanticVersion updateFound))
                {
                    if (Constants.ApiVersion.IsPrerelease() && updateFound.IsNewerThan(Constants.ApiVersion))
                    {
                        this.Monitor.Log("A new version of SMAPI was detected last time you played.", LogLevel.Error);
                        this.Monitor.Log($"You can update to {updateFound}: https://smapi.io.", LogLevel.Error);
                        this.Monitor.Log("Press any key to continue playing anyway. (This only appears when using a SMAPI beta.)", LogLevel.Info);
                        Console.ReadKey();
                    }
                }
                File.Delete(Constants.UpdateMarker);
            }

            // show details if game crashed during last session
            if (File.Exists(Constants.FatalCrashMarker))
            {
                this.Monitor.Log("The game crashed last time you played. If it happens repeatedly, see 'get help' on https://smapi.io.", LogLevel.Error);
                this.Monitor.Log("If you ask for help, make sure to share your SMAPI log: https://smapi.io/log.", LogLevel.Error);
                this.Monitor.Log("Press any key to delete the crash data and continue playing.", LogLevel.Info);
                Console.ReadKey();
                File.Delete(Constants.FatalCrashLog);
                File.Delete(Constants.FatalCrashMarker);
            }

            // add headers
            if (this.Settings.DeveloperMode)
                this.Monitor.Log($"You have SMAPI for developers, so the console will be much more verbose. You can disable developer mode by installing the non-developer version of SMAPI, or by editing {Constants.ApiConfigPath}.", LogLevel.Info);
            if (!this.Settings.CheckForUpdates)
                this.Monitor.Log($"You configured SMAPI to not check for updates. Running an old version of SMAPI is not recommended. You can enable update checks by reinstalling SMAPI or editing {Constants.ApiConfigPath}.", LogLevel.Warn);
            if (!this.Monitor.WriteToConsole)
                this.Monitor.Log("Writing to the terminal is disabled because the --no-terminal argument was received. This usually means launching the terminal failed.", LogLevel.Warn);
            this.Monitor.VerboseLog("Verbose logging enabled.");

            // update window titles
            this.GameInstance.Window.Title = $"Stardew Valley {Constants.GameVersion} - running SMAPI {Constants.ApiVersion}";
            Console.Title = $"SMAPI {Constants.ApiVersion} - running Stardew Valley {Constants.GameVersion}";

            // start game
            this.Monitor.Log("Starting game...", LogLevel.Debug);
            try
            {
                this.IsGameRunning = true;
                StardewValley.Program.releaseBuild = true; // game's debug logic interferes with SMAPI opening the game window
                this.GameInstance.Run();
            }
            catch (InvalidOperationException ex) when (ex.Source == "Microsoft.Xna.Framework.Xact" && ex.StackTrace.Contains("Microsoft.Xna.Framework.Audio.AudioEngine..ctor"))
            {
                this.Monitor.Log("The game couldn't load audio. Do you have speakers or headphones plugged in?", LogLevel.Error);
                this.Monitor.Log($"Technical details: {ex.GetLogSummary()}", LogLevel.Trace);
                this.PressAnyKeyToExit();
            }
            catch (FileNotFoundException ex) when (ex.Message == "Could not find file 'C:\\Program Files (x86)\\Steam\\SteamApps\\common\\Stardew Valley\\Content\\XACT\\FarmerSounds.xgs'.") // path in error is hardcoded regardless of install path
            {
                this.Monitor.Log("The game can't find its Content\\XACT\\FarmerSounds.xgs file. You can usually fix this by resetting your content files (see https://smapi.io/troubleshoot#reset-content ), or by uninstalling and reinstalling the game.", LogLevel.Error);
                this.Monitor.Log($"Technical details: {ex.GetLogSummary()}", LogLevel.Trace);
                this.PressAnyKeyToExit();
            }
            catch (Exception ex)
            {
                this.MonitorForGame.Log($"The game failed to launch: {ex.GetLogSummary()}", LogLevel.Error);
                this.PressAnyKeyToExit();
            }
            finally
            {
                this.Dispose();
            }
        }

        /// <summary>Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources.</summary>
        public void Dispose()
        {
            // skip if already disposed
            if (this.IsDisposed)
                return;
            this.IsDisposed = true;
            this.Monitor.Log("Disposing...", LogLevel.Trace);

            // dispose mod data
            foreach (IModMetadata mod in this.ModRegistry.GetAll())
            {
                try
                {
                    (mod.Mod as IDisposable)?.Dispose();
                }
                catch (Exception ex)
                {
                    mod.LogAsMod($"Mod failed during disposal: {ex.GetLogSummary()}.", LogLevel.Warn);
                }
            }

            // dispose core components
            this.IsGameRunning = false;
            this.ConsoleManager?.Dispose();
            this.ContentCore?.Dispose();
            this.CancellationToken?.Dispose();
            this.GameInstance?.Dispose();
            this.LogFile?.Dispose();

            // end game (moved from Game1.OnExiting to let us clean up first)
            Process.GetCurrentProcess().Kill();
        }


        /*********
        ** Private methods
        *********/
        /// <summary>Initialize mods before the first game asset is loaded. At this point the core content managers are loaded (so mods can load their own assets), but the game is mostly uninitialized.</summary>
        private void InitializeBeforeFirstAssetLoaded()
        {
            if (this.CancellationToken.IsCancellationRequested)
            {
                this.Monitor.Log("SMAPI shutting down: aborting initialization.", LogLevel.Warn);
                return;
            }

            // init TMX support
            try
            {
                xTile.Format.FormatManager.Instance.RegisterMapFormat(new TMXTile.TMXFormat(Game1.tileSize / Game1.pixelZoom, Game1.tileSize / Game1.pixelZoom, Game1.pixelZoom, Game1.pixelZoom));
            }
            catch (Exception ex)
            {
                this.Monitor.Log("SMAPI couldn't load TMX support. Some mods may not work correctly.", LogLevel.Warn);
                this.Monitor.Log($"Technical details: {ex.GetLogSummary()}", LogLevel.Trace);
            }

            // load mod data
            ModToolkit toolkit = new ModToolkit();
            ModDatabase modDatabase = toolkit.GetModDatabase(Constants.ApiMetadataPath);

            // load mods
            {
                this.Monitor.Log("Loading mod metadata...", LogLevel.Trace);
                ModResolver resolver = new ModResolver();

                // log loose files
                {
                    string[] looseFiles = new DirectoryInfo(this.ModsPath).GetFiles().Select(p => p.Name).ToArray();
                    if (looseFiles.Any())
                        this.Monitor.Log($"  Ignored loose files: {string.Join(", ", looseFiles.OrderBy(p => p, StringComparer.InvariantCultureIgnoreCase))}", LogLevel.Trace);
                }

                // load manifests
                IModMetadata[] mods = resolver.ReadManifests(toolkit, this.ModsPath, modDatabase).ToArray();

                // filter out ignored mods
                foreach (IModMetadata mod in mods.Where(p => p.IsIgnored))
                    this.Monitor.Log($"  Skipped {mod.GetRelativePathWithRoot()} (folder name starts with a dot).", LogLevel.Trace);
                mods = mods.Where(p => !p.IsIgnored).ToArray();

                // load mods
                resolver.ValidateManifests(mods, Constants.ApiVersion, toolkit.GetUpdateUrl);
                mods = resolver.ProcessDependencies(mods, modDatabase).ToArray();
                this.LoadMods(mods, this.Toolkit.JsonHelper, this.ContentCore, modDatabase);

                // check for updates
                this.CheckForUpdatesAsync(mods);
            }

            // update window titles
            int modsLoaded = this.ModRegistry.GetAll().Count();
            this.GameInstance.Window.Title = $"Stardew Valley {Constants.GameVersion} - running SMAPI {Constants.ApiVersion} with {modsLoaded} mods";
            Console.Title = $"SMAPI {Constants.ApiVersion} - running Stardew Valley {Constants.GameVersion} with {modsLoaded} mods";
        }

        /// <summary>Initialize SMAPI and mods after the game starts.</summary>
        private void InitializeAfterGameStart()
        {
            // validate XNB integrity
            if (!this.ValidateContentIntegrity())
                this.Monitor.Log("SMAPI found problems in your game's content files which are likely to cause errors or crashes. Consider uninstalling XNB mods or reinstalling the game.", LogLevel.Error);

            // start SMAPI console
            new Thread(this.RunConsoleLoop).Start();
        }

        /// <summary>Handle the game changing locale.</summary>
        private void OnLocaleChanged()
        {
            this.ContentCore.OnLocaleChanged();

            // get locale
            string locale = this.ContentCore.GetLocale();
            LocalizedContentManager.LanguageCode languageCode = this.ContentCore.Language;

            // update core translations
            this.Translator.SetLocale(locale, languageCode);

            // update mod translation helpers
            foreach (IModMetadata mod in this.ModRegistry.GetAll())
                mod.Translations.SetLocale(locale, languageCode);
        }

        /// <summary>Run a loop handling console input.</summary>
        [SuppressMessage("ReSharper", "FunctionNeverReturns", Justification = "The thread is aborted when the game exits.")]
        private void RunConsoleLoop()
        {
            // prepare console
            this.Monitor.Log("Type 'help' for help, or 'help <cmd>' for a command's usage", LogLevel.Info);
            this.GameInstance.CommandManager.Add(null, "help", "Lists command documentation.\n\nUsage: help\nLists all available commands.\n\nUsage: help <cmd>\n- cmd: The name of a command whose documentation to display.", this.HandleCommand);
            this.GameInstance.CommandManager.Add(null, "reload_i18n", "Reloads translation files for all mods.\n\nUsage: reload_i18n", this.HandleCommand);

            // start handling command line input
            Thread inputThread = new Thread(() =>
            {
                while (true)
                {
                    // get input
                    string input = Console.ReadLine();
                    if (string.IsNullOrWhiteSpace(input))
                        continue;

                    // handle command
                    this.Monitor.LogUserInput(input);
                    this.GameInstance.CommandQueue.Enqueue(input);
                }
            });
            inputThread.Start();

            // keep console thread alive while the game is running
            while (this.IsGameRunning && !this.CancellationToken.IsCancellationRequested)
                Thread.Sleep(1000 / 10);
            if (inputThread.ThreadState == ThreadState.Running)
                inputThread.Abort();
        }

        /// <summary>Look for common issues with the game's XNB content, and log warnings if anything looks broken or outdated.</summary>
        /// <returns>Returns whether all integrity checks passed.</returns>
        private bool ValidateContentIntegrity()
        {
            this.Monitor.Log("Detecting common issues...", LogLevel.Trace);
            bool issuesFound = false;

            // object format (commonly broken by outdated files)
            {
                // detect issues
                bool hasObjectIssues = false;
                void LogIssue(int id, string issue) => this.Monitor.Log($@"Detected issue: item #{id} in Content\Data\ObjectInformation.xnb is invalid ({issue}).", LogLevel.Trace);
                foreach (KeyValuePair<int, string> entry in Game1.objectInformation)
                {
                    // must not be empty
                    if (string.IsNullOrWhiteSpace(entry.Value))
                    {
                        LogIssue(entry.Key, "entry is empty");
                        hasObjectIssues = true;
                        continue;
                    }

                    // require core fields
                    string[] fields = entry.Value.Split('/');
                    if (fields.Length < Object.objectInfoDescriptionIndex + 1)
                    {
                        LogIssue(entry.Key, "too few fields for an object");
                        hasObjectIssues = true;
                        continue;
                    }

                    // check min length for specific types
                    switch (fields[Object.objectInfoTypeIndex].Split(new[] { ' ' }, 2)[0])
                    {
                        case "Cooking":
                            if (fields.Length < Object.objectInfoBuffDurationIndex + 1)
                            {
                                LogIssue(entry.Key, "too few fields for a cooking item");
                                hasObjectIssues = true;
                            }
                            break;
                    }
                }

                // log error
                if (hasObjectIssues)
                {
                    issuesFound = true;
                    this.Monitor.Log(@"Your Content\Data\ObjectInformation.xnb file seems to be broken or outdated.", LogLevel.Warn);
                }
            }

            return !issuesFound;
        }

        /// <summary>Asynchronously check for a new version of SMAPI and any installed mods, and print alerts to the console if an update is available.</summary>
        /// <param name="mods">The mods to include in the update check (if eligible).</param>
        private void CheckForUpdatesAsync(IModMetadata[] mods)
        {
            if (!this.Settings.CheckForUpdates)
                return;

            new Thread(() =>
            {
                // create client
                string url = this.Settings.WebApiBaseUrl;
#if !SMAPI_FOR_WINDOWS
                url = url.Replace("https://", "http://"); // workaround for OpenSSL issues with the game's bundled Mono on Linux/Mac
#endif
                WebApiClient client = new WebApiClient(url, Constants.ApiVersion);
                this.Monitor.Log("Checking for updates...", LogLevel.Trace);

                // check SMAPI version
                ISemanticVersion updateFound = null;
                try
                {
                    // fetch update check
                    ModEntryModel response = client.GetModInfo(new[] { new ModSearchEntryModel("Pathoschild.SMAPI", Constants.ApiVersion, new[] { $"GitHub:{this.Settings.GitHubProjectName}" }) }, apiVersion: Constants.ApiVersion, gameVersion: Constants.GameVersion, platform: Constants.Platform).Single().Value;
                    if (response.SuggestedUpdate != null)
                        this.Monitor.Log($"You can update SMAPI to {response.SuggestedUpdate.Version}: {Constants.HomePageUrl}", LogLevel.Alert);
                    else
                        this.Monitor.Log("   SMAPI okay.", LogLevel.Trace);

                    updateFound = response.SuggestedUpdate?.Version;

                    // show errors
                    if (response.Errors.Any())
                    {
                        this.Monitor.Log("Couldn't check for a new version of SMAPI. This won't affect your game, but you may not be notified of new versions if this keeps happening.", LogLevel.Warn);
                        this.Monitor.Log($"Error: {string.Join("\n", response.Errors)}", LogLevel.Trace);
                    }
                }
                catch (Exception ex)
                {
                    this.Monitor.Log("Couldn't check for a new version of SMAPI. This won't affect your game, but you won't be notified of new versions if this keeps happening.", LogLevel.Warn);
                    this.Monitor.Log(ex is WebException && ex.InnerException == null
                        ? $"Error: {ex.Message}"
                        : $"Error: {ex.GetLogSummary()}", LogLevel.Trace
                    );
                }

                // show update message on next launch
                if (updateFound != null)
                    File.WriteAllText(Constants.UpdateMarker, updateFound.ToString());

                // check mod versions
                if (mods.Any())
                {
                    try
                    {
                        HashSet<string> suppressUpdateChecks = new HashSet<string>(this.Settings.SuppressUpdateChecks, StringComparer.InvariantCultureIgnoreCase);

                        // prepare search model
                        List<ModSearchEntryModel> searchMods = new List<ModSearchEntryModel>();
                        foreach (IModMetadata mod in mods)
                        {
                            if (!mod.HasID() || suppressUpdateChecks.Contains(mod.Manifest.UniqueID))
                                continue;

                            string[] updateKeys = mod
                                .GetUpdateKeys(validOnly: true)
                                .Select(p => p.ToString())
                                .ToArray();
                            searchMods.Add(new ModSearchEntryModel(mod.Manifest.UniqueID, mod.Manifest.Version, updateKeys.ToArray(), isBroken: mod.Status == ModMetadataStatus.Failed));
                        }

                        // fetch results
                        this.Monitor.Log($"   Checking for updates to {searchMods.Count} mods...", LogLevel.Trace);
                        IDictionary<string, ModEntryModel> results = client.GetModInfo(searchMods.ToArray(), apiVersion: Constants.ApiVersion, gameVersion: Constants.GameVersion, platform: Constants.Platform);

                        // extract update alerts & errors
                        var updates = new List<Tuple<IModMetadata, ISemanticVersion, string>>();
                        var errors = new StringBuilder();
                        foreach (IModMetadata mod in mods.OrderBy(p => p.DisplayName))
                        {
                            // link to update-check data
                            if (!mod.HasID() || !results.TryGetValue(mod.Manifest.UniqueID, out ModEntryModel result))
                                continue;
                            mod.SetUpdateData(result);

                            // handle errors
                            if (result.Errors != null && result.Errors.Any())
                            {
                                errors.AppendLine(result.Errors.Length == 1
                                    ? $"   {mod.DisplayName}: {result.Errors[0]}"
                                    : $"   {mod.DisplayName}:\n      - {string.Join("\n      - ", result.Errors)}"
                                );
                            }

                            // handle update
                            if (result.SuggestedUpdate != null)
                                updates.Add(Tuple.Create(mod, result.SuggestedUpdate.Version, result.SuggestedUpdate.Url));
                        }

                        // show update errors
                        if (errors.Length != 0)
                            this.Monitor.Log("Got update-check errors for some mods:\n" + errors.ToString().TrimEnd(), LogLevel.Trace);

                        // show update alerts
                        if (updates.Any())
                        {
                            this.Monitor.Newline();
                            this.Monitor.Log($"You can update {updates.Count} mod{(updates.Count != 1 ? "s" : "")}:", LogLevel.Alert);
                            foreach (var entry in updates)
                            {
                                IModMetadata mod = entry.Item1;
                                ISemanticVersion newVersion = entry.Item2;
                                string newUrl = entry.Item3;
                                this.Monitor.Log($"   {mod.DisplayName} {newVersion}: {newUrl}", LogLevel.Alert);
                            }
                        }
                        else
                            this.Monitor.Log("   All mods up to date.", LogLevel.Trace);
                    }
                    catch (Exception ex)
                    {
                        this.Monitor.Log("Couldn't check for new mod versions. This won't affect your game, but you won't be notified of mod updates if this keeps happening.", LogLevel.Warn);
                        this.Monitor.Log(ex is WebException && ex.InnerException == null
                            ? ex.Message
                            : ex.ToString(), LogLevel.Trace
                        );
                    }
                }
            }).Start();
        }

        /// <summary>Create a directory path if it doesn't exist.</summary>
        /// <param name="path">The directory path.</param>
        private void VerifyPath(string path)
        {
            try
            {
                if (!Directory.Exists(path))
                    Directory.CreateDirectory(path);
            }
            catch (Exception ex)
            {
                // note: this happens before this.Monitor is initialized
                Console.WriteLine($"Couldn't create a path: {path}\n\n{ex.GetLogSummary()}");
            }
        }

        /// <summary>Load and hook up the given mods.</summary>
        /// <param name="mods">The mods to load.</param>
        /// <param name="jsonHelper">The JSON helper with which to read mods' JSON files.</param>
        /// <param name="contentCore">The content manager to use for mod content.</param>
        /// <param name="modDatabase">Handles access to SMAPI's internal mod metadata list.</param>
        private void LoadMods(IModMetadata[] mods, JsonHelper jsonHelper, ContentCoordinator contentCore, ModDatabase modDatabase)
        {
            this.Monitor.Log("Loading mods...", LogLevel.Trace);

            // load mods
            IDictionary<IModMetadata, Tuple<string, string>> skippedMods = new Dictionary<IModMetadata, Tuple<string, string>>();
            using (AssemblyLoader modAssemblyLoader = new AssemblyLoader(Constants.Platform, this.Monitor, this.Settings.ParanoidWarnings))
            {
                // init
                HashSet<string> suppressUpdateChecks = new HashSet<string>(this.Settings.SuppressUpdateChecks, StringComparer.InvariantCultureIgnoreCase);
                InterfaceProxyFactory proxyFactory = new InterfaceProxyFactory();
                void LogSkip(IModMetadata mod, string errorPhrase, string errorDetails)
                {
                    skippedMods[mod] = Tuple.Create(errorPhrase, errorDetails);
                    if (mod.Status != ModMetadataStatus.Failed)
                        mod.SetStatus(ModMetadataStatus.Failed, errorPhrase);
                }

                // load mods
                foreach (IModMetadata contentPack in mods)
                {
                    if (!this.TryLoadMod(contentPack, mods, modAssemblyLoader, proxyFactory, jsonHelper, contentCore, modDatabase, suppressUpdateChecks, out string errorPhrase, out string errorDetails))
                        LogSkip(contentPack, errorPhrase, errorDetails);
                }
            }
            IModMetadata[] loaded = this.ModRegistry.GetAll().ToArray();
            IModMetadata[] loadedContentPacks = loaded.Where(p => p.IsContentPack).ToArray();
            IModMetadata[] loadedMods = loaded.Where(p => !p.IsContentPack).ToArray();

            // unlock content packs
            this.ModRegistry.AreAllModsLoaded = true;

            // log loaded mods
            this.Monitor.Log($"Loaded {loadedMods.Length} mods" + (loadedMods.Length > 0 ? ":" : "."), LogLevel.Info);
            foreach (IModMetadata metadata in loadedMods.OrderBy(p => p.DisplayName))
            {
                IManifest manifest = metadata.Manifest;
                this.Monitor.Log(
                    $"   {metadata.DisplayName} {manifest.Version}"
                    + (!string.IsNullOrWhiteSpace(manifest.Author) ? $" by {manifest.Author}" : "")
                    + (!string.IsNullOrWhiteSpace(manifest.Description) ? $" | {manifest.Description}" : ""),
                    LogLevel.Info
                );
            }
            this.Monitor.Newline();

            // log loaded content packs
            if (loadedContentPacks.Any())
            {
                string GetModDisplayName(string id) => loadedMods.FirstOrDefault(p => p.HasID(id))?.DisplayName;

                this.Monitor.Log($"Loaded {loadedContentPacks.Length} content packs:", LogLevel.Info);
                foreach (IModMetadata metadata in loadedContentPacks.OrderBy(p => p.DisplayName))
                {
                    IManifest manifest = metadata.Manifest;
                    this.Monitor.Log(
                        $"   {metadata.DisplayName} {manifest.Version}"
                        + (!string.IsNullOrWhiteSpace(manifest.Author) ? $" by {manifest.Author}" : "")
                        + $" | for {GetModDisplayName(metadata.Manifest.ContentPackFor.UniqueID)}"
                        + (!string.IsNullOrWhiteSpace(manifest.Description) ? $" | {manifest.Description}" : ""),
                        LogLevel.Info
                    );
                }
                this.Monitor.Newline();
            }

            // log mod warnings
            this.LogModWarnings(loaded, skippedMods);

            // initialize translations
            this.ReloadTranslations(loaded);

            // initialize loaded non-content-pack mods
            foreach (IModMetadata metadata in loadedMods)
            {
                // add interceptors
                if (metadata.Mod.Helper.Content is ContentHelper helper)
                {
                    // ReSharper disable SuspiciousTypeConversion.Global
                    if (metadata.Mod is IAssetEditor editor)
                        this.ContentCore.Editors.Add(new ModLinked<IAssetEditor>(metadata, editor));
                    if (metadata.Mod is IAssetLoader loader)
                        this.ContentCore.Loaders.Add(new ModLinked<IAssetLoader>(metadata, loader));
                    // ReSharper restore SuspiciousTypeConversion.Global

                    helper.ObservableAssetEditors.CollectionChanged += (sender, e) => this.OnInterceptorsChanged(metadata, e.NewItems?.Cast<IAssetEditor>(), e.OldItems?.Cast<IAssetEditor>(), this.ContentCore.Editors);
                    helper.ObservableAssetLoaders.CollectionChanged += (sender, e) => this.OnInterceptorsChanged(metadata, e.NewItems?.Cast<IAssetLoader>(), e.OldItems?.Cast<IAssetLoader>(), this.ContentCore.Loaders);
                }

                // call entry method
                try
                {
                    IMod mod = metadata.Mod;
                    mod.Entry(mod.Helper);
                }
                catch (Exception ex)
                {
                    metadata.LogAsMod($"Mod crashed on entry and might not work correctly. Technical details:\n{ex.GetLogSummary()}", LogLevel.Error);
                }

                // get mod API
                try
                {
                    object api = metadata.Mod.GetApi();
                    if (api != null && !api.GetType().IsPublic)
                    {
                        api = null;
                        this.Monitor.Log($"{metadata.DisplayName} provides an API instance with a non-public type. This isn't currently supported, so the API won't be available to other mods.", LogLevel.Warn);
                    }

                    if (api != null)
                        this.Monitor.Log($"   Found mod-provided API ({api.GetType().FullName}).", LogLevel.Trace);
                    metadata.SetApi(api);
                }
                catch (Exception ex)
                {
                    this.Monitor.Log($"Failed loading mod-provided API for {metadata.DisplayName}. Integrations with other mods may not work. Error: {ex.GetLogSummary()}", LogLevel.Error);
                }
            }

            // invalidate cache entries when needed
            // (These listeners are registered after Entry to avoid repeatedly reloading assets as mods initialize.)
            foreach (IModMetadata metadata in loadedMods)
            {
                if (metadata.Mod.Helper.Content is ContentHelper helper)
                {
                    helper.ObservableAssetEditors.CollectionChanged += (sender, e) => this.GameInstance.OnAssetInterceptorsChanged(metadata, e.NewItems, e.OldItems);
                    helper.ObservableAssetLoaders.CollectionChanged += (sender, e) => this.GameInstance.OnAssetInterceptorsChanged(metadata, e.NewItems, e.OldItems);
                }
            }

            // unlock mod integrations
            this.ModRegistry.AreAllModsInitialized = true;
        }

        /// <summary>Handle a mod adding or removing asset interceptors.</summary>
        /// <typeparam name="T">The asset interceptor type (one of <see cref="IAssetEditor"/> or <see cref="IAssetLoader"/>).</typeparam>
        /// <param name="mod">The mod metadata.</param>
        /// <param name="added">The interceptors that were added.</param>
        /// <param name="removed">The interceptors that were removed.</param>
        /// <param name="list">The list to update.</param>
        private void OnInterceptorsChanged<T>(IModMetadata mod, IEnumerable<T> added, IEnumerable<T> removed, IList<ModLinked<T>> list)
        {
            foreach (T interceptor in added ?? new T[0])
                list.Add(new ModLinked<T>(mod, interceptor));

            foreach (T interceptor in removed ?? new T[0])
            {
                foreach (ModLinked<T> entry in list.Where(p => p.Mod == mod && object.ReferenceEquals(p.Data, interceptor)).ToArray())
                    list.Remove(entry);
            }
        }

        /// <summary>Load a given mod.</summary>
        /// <param name="mod">The mod to load.</param>
        /// <param name="mods">The mods being loaded.</param>
        /// <param name="assemblyLoader">Preprocesses and loads mod assemblies</param>
        /// <param name="proxyFactory">Generates proxy classes to access mod APIs through an arbitrary interface.</param>
        /// <param name="jsonHelper">The JSON helper with which to read mods' JSON files.</param>
        /// <param name="contentCore">The content manager to use for mod content.</param>
        /// <param name="modDatabase">Handles access to SMAPI's internal mod metadata list.</param>
        /// <param name="suppressUpdateChecks">The mod IDs to ignore when validating update keys.</param>
        /// <param name="errorReasonPhrase">The user-facing reason phrase explaining why the mod couldn't be loaded (if applicable).</param>
        /// <param name="errorDetails">More detailed details about the error intended for developers (if any).</param>
        /// <returns>Returns whether the mod was successfully loaded.</returns>
        private bool TryLoadMod(IModMetadata mod, IModMetadata[] mods, AssemblyLoader assemblyLoader, InterfaceProxyFactory proxyFactory, JsonHelper jsonHelper, ContentCoordinator contentCore, ModDatabase modDatabase, HashSet<string> suppressUpdateChecks, out string errorReasonPhrase, out string errorDetails)
        {
            errorDetails = null;

            // log entry
            {
                string relativePath = mod.GetRelativePathWithRoot();
                if (mod.IsContentPack)
                    this.Monitor.Log($"   {mod.DisplayName} (from {relativePath}) [content pack]...", LogLevel.Trace);
                else if (mod.Manifest?.EntryDll != null)
                    this.Monitor.Log($"   {mod.DisplayName} (from {relativePath}{Path.DirectorySeparatorChar}{mod.Manifest.EntryDll})...", LogLevel.Trace); // don't use Path.Combine here, since EntryDLL might not be valid
                else
                    this.Monitor.Log($"   {mod.DisplayName} (from {relativePath})...", LogLevel.Trace);
            }

            // add warning for missing update key
            if (mod.HasID() && !suppressUpdateChecks.Contains(mod.Manifest.UniqueID) && !mod.HasValidUpdateKeys())
                mod.SetWarning(ModWarning.NoUpdateKeys);

            // validate status
            if (mod.Status == ModMetadataStatus.Failed)
            {
                this.Monitor.Log($"      Failed: {mod.Error}", LogLevel.Trace);
                errorReasonPhrase = mod.Error;
                return false;
            }

            // validate dependencies
            // Although dependencies are validated before mods are loaded, a dependency may have failed to load.
            if (mod.Manifest.Dependencies?.Any() == true)
            {
                foreach (IManifestDependency dependency in mod.Manifest.Dependencies.Where(p => p.IsRequired))
                {
                    if (this.ModRegistry.Get(dependency.UniqueID) == null)
                    {
                        string dependencyName = mods
                            .FirstOrDefault(otherMod => otherMod.HasID(dependency.UniqueID))
                            ?.DisplayName ?? dependency.UniqueID;
                        errorReasonPhrase = $"it needs the '{dependencyName}' mod, which couldn't be loaded.";
                        return false;
                    }
                }
            }

            // load as content pack
            if (mod.IsContentPack)
            {
                IManifest manifest = mod.Manifest;
                IMonitor monitor = this.GetSecondaryMonitor(mod.DisplayName);
                IContentHelper contentHelper = new ContentHelper(this.ContentCore, mod.DirectoryPath, manifest.UniqueID, mod.DisplayName, monitor);
                TranslationHelper translationHelper = new TranslationHelper(manifest.UniqueID, contentCore.GetLocale(), contentCore.Language);
                IContentPack contentPack = new ContentPack(mod.DirectoryPath, manifest, contentHelper, translationHelper, jsonHelper);
                mod.SetMod(contentPack, monitor, translationHelper);
                this.ModRegistry.Add(mod);

                errorReasonPhrase = null;
                return true;
            }

            // load as mod
            else
            {
                IManifest manifest = mod.Manifest;

                // load mod
                string assemblyPath = manifest?.EntryDll != null
                    ? Path.Combine(mod.DirectoryPath, manifest.EntryDll)
                    : null;
                Assembly modAssembly;
                try
                {
                    modAssembly = assemblyLoader.Load(mod, assemblyPath, assumeCompatible: mod.DataRecord?.Status == ModStatus.AssumeCompatible);
                    this.ModRegistry.TrackAssemblies(mod, modAssembly);
                }
                catch (IncompatibleInstructionException) // details already in trace logs
                {
                    string[] updateUrls = new[] { modDatabase.GetModPageUrlFor(manifest.UniqueID), "https://smapi.io/mods" }.Where(p => p != null).ToArray();
                    errorReasonPhrase = $"it's no longer compatible. Please check for a new version at {string.Join(" or ", updateUrls)}";
                    return false;
                }
                catch (SAssemblyLoadFailedException ex)
                {
                    errorReasonPhrase = $"it DLL couldn't be loaded: {ex.Message}";
                    return false;
                }
                catch (Exception ex)
                {
                    errorReasonPhrase = "its DLL couldn't be loaded.";
                    errorDetails = $"Error: {ex.GetLogSummary()}";
                    return false;
                }

                // initialize mod
                try
                {
                    // get mod instance
                    if (!this.TryLoadModEntry(modAssembly, out Mod modEntry, out errorReasonPhrase))
                        return false;

                    // get content packs
                    IContentPack[] GetContentPacks()
                    {
                        if (!this.ModRegistry.AreAllModsLoaded)
                            throw new InvalidOperationException("Can't access content packs before SMAPI finishes loading mods.");

                        return this.ModRegistry
                            .GetAll(assemblyMods: false)
                            .Where(p => p.IsContentPack && mod.HasID(p.Manifest.ContentPackFor.UniqueID))
                            .Select(p => p.ContentPack)
                            .ToArray();
                    }

                    // init mod helpers
                    IMonitor monitor = this.GetSecondaryMonitor(mod.DisplayName);
                    TranslationHelper translationHelper = new TranslationHelper(manifest.UniqueID, contentCore.GetLocale(), contentCore.Language);
                    IModHelper modHelper;
                    {
                        IContentPack CreateFakeContentPack(string packDirPath, IManifest packManifest)
                        {
                            IMonitor packMonitor = this.GetSecondaryMonitor(packManifest.Name);
                            IContentHelper packContentHelper = new ContentHelper(contentCore, packDirPath, packManifest.UniqueID, packManifest.Name, packMonitor);
                            ITranslationHelper packTranslationHelper = new TranslationHelper(packManifest.UniqueID, contentCore.GetLocale(), contentCore.Language);
                            return new ContentPack(packDirPath, packManifest, packContentHelper, packTranslationHelper, this.Toolkit.JsonHelper);
                        }

                        IModEvents events = new ModEvents(mod, this.EventManager);
                        ICommandHelper commandHelper = new CommandHelper(mod, this.GameInstance.CommandManager);
                        IContentHelper contentHelper = new ContentHelper(contentCore, mod.DirectoryPath, manifest.UniqueID, mod.DisplayName, monitor);
                        IContentPackHelper contentPackHelper = new ContentPackHelper(manifest.UniqueID, new Lazy<IContentPack[]>(GetContentPacks), CreateFakeContentPack);
                        IDataHelper dataHelper = new DataHelper(manifest.UniqueID, mod.DirectoryPath, jsonHelper);
                        IReflectionHelper reflectionHelper = new ReflectionHelper(manifest.UniqueID, mod.DisplayName, this.Reflection);
                        IModRegistry modRegistryHelper = new ModRegistryHelper(manifest.UniqueID, this.ModRegistry, proxyFactory, monitor);
                        IMultiplayerHelper multiplayerHelper = new MultiplayerHelper(manifest.UniqueID, this.GameInstance.Multiplayer);

                        modHelper = new ModHelper(manifest.UniqueID, mod.DirectoryPath, this.GameInstance.Input, events, contentHelper, contentPackHelper, commandHelper, dataHelper, modRegistryHelper, reflectionHelper, multiplayerHelper, translationHelper);
                    }

                    // init mod
                    modEntry.ModManifest = manifest;
                    modEntry.Helper = modHelper;
                    modEntry.Monitor = monitor;

                    // track mod
                    mod.SetMod(modEntry, translationHelper);
                    this.ModRegistry.Add(mod);
                    return true;
                }
                catch (Exception ex)
                {
                    errorReasonPhrase = $"initialization failed:\n{ex.GetLogSummary()}";
                    return false;
                }
            }
        }

        /// <summary>Write a summary of mod warnings to the console and log.</summary>
        /// <param name="mods">The loaded mods.</param>
        /// <param name="skippedMods">The mods which were skipped, along with the friendly and developer reasons.</param>
        private void LogModWarnings(IEnumerable<IModMetadata> mods, IDictionary<IModMetadata, Tuple<string, string>> skippedMods)
        {
            // get mods with warnings
            IModMetadata[] modsWithWarnings = mods.Where(p => p.Warnings != ModWarning.None).ToArray();
            if (!modsWithWarnings.Any() && !skippedMods.Any())
                return;

            // log intro
            {
                int count = modsWithWarnings.Union(skippedMods.Keys).Count();
                this.Monitor.Log($"Found {count} mod{(count == 1 ? "" : "s")} with warnings:", LogLevel.Info);
            }

            // log skipped mods
            if (skippedMods.Any())
            {
                // get logging logic
                HashSet<string> logged = new HashSet<string>();
                void LogSkippedMod(IModMetadata mod, string errorReason, string errorDetails)
                {
                    string message = $"      - {mod.DisplayName}{(mod.Manifest?.Version != null ? " " + mod.Manifest.Version.ToString() : "")} because {errorReason}";

                    if (logged.Add($"{message}|{errorDetails}"))
                    {
                        this.Monitor.Log(message, LogLevel.Error);
                        if (errorDetails != null)
                            this.Monitor.Log($"        ({errorDetails})", LogLevel.Trace);
                    }
                }

                // find skipped dependencies
                KeyValuePair<IModMetadata, Tuple<string, string>>[] skippedDependencies;
                {
                    HashSet<string> skippedDependencyIds = new HashSet<string>(StringComparer.InvariantCultureIgnoreCase);
                    HashSet<string> skippedModIds = new HashSet<string>(from mod in skippedMods where mod.Key.HasID() select mod.Key.Manifest.UniqueID, StringComparer.InvariantCultureIgnoreCase);
                    foreach (IModMetadata mod in skippedMods.Keys)
                    {
                        foreach (string requiredId in skippedModIds.Intersect(mod.GetRequiredModIds()))
                            skippedDependencyIds.Add(requiredId);
                    }
                    skippedDependencies = skippedMods.Where(p => p.Key.HasID() && skippedDependencyIds.Contains(p.Key.Manifest.UniqueID)).ToArray();
                }

                // log skipped mods
                this.Monitor.Log("   Skipped mods", LogLevel.Error);
                this.Monitor.Log("   " + "".PadRight(50, '-'), LogLevel.Error);
                this.Monitor.Log("      These mods could not be added to your game.", LogLevel.Error);
                this.Monitor.Newline();

                if (skippedDependencies.Any())
                {
                    foreach (var pair in skippedDependencies.OrderBy(p => p.Key.DisplayName))
                        LogSkippedMod(pair.Key, pair.Value.Item1, pair.Value.Item2);
                    this.Monitor.Newline();
                }

                foreach (var pair in skippedMods.OrderBy(p => p.Key.DisplayName))
                    LogSkippedMod(pair.Key, pair.Value.Item1, pair.Value.Item2);
                this.Monitor.Newline();
            }

            // log warnings
            if (modsWithWarnings.Any())
            {
                // issue block format logic
                void LogWarningGroup(ModWarning warning, LogLevel logLevel, string heading, params string[] blurb)
                {
                    IModMetadata[] matches = modsWithWarnings
                        .Where(mod => mod.HasUnsuppressWarning(warning))
                        .ToArray();
                    if (!matches.Any())
                        return;

                    this.Monitor.Log("   " + heading, logLevel);
                    this.Monitor.Log("   " + "".PadRight(50, '-'), logLevel);
                    foreach (string line in blurb)
                        this.Monitor.Log("      " + line, logLevel);
                    this.Monitor.Newline();
                    foreach (IModMetadata match in matches)
                        this.Monitor.Log($"      - {match.DisplayName}", logLevel);
                    this.Monitor.Newline();
                }

                // supported issues
                LogWarningGroup(ModWarning.BrokenCodeLoaded, LogLevel.Error, "Broken mods",
                    "These mods have broken code, but you configured SMAPI to load them anyway. This may cause bugs,",
                    "errors, or crashes in-game."
                );
                LogWarningGroup(ModWarning.ChangesSaveSerializer, LogLevel.Warn, "Changed save serializer",
                    "These mods change the save serializer. They may corrupt your save files, or make them unusable if",
                    "you uninstall these mods."
                );
                if (this.Settings.ParanoidWarnings)
                {
                    LogWarningGroup(ModWarning.AccessesConsole, LogLevel.Warn, "Accesses the console directly",
                        "These mods directly access the SMAPI console, and you enabled paranoid warnings. (Note that this may be",
                        "legitimate and innocent usage; this warning is meaningless without further investigation.)"
                    );
                    LogWarningGroup(ModWarning.AccessesFilesystem, LogLevel.Warn, "Accesses filesystem directly",
                        "These mods directly access the filesystem, and you enabled paranoid warnings. (Note that this may be",
                        "legitimate and innocent usage; this warning is meaningless without further investigation.)"
                    );
                    LogWarningGroup(ModWarning.AccessesShell, LogLevel.Warn, "Accesses shell/process directly",
                        "These mods directly access the OS shell or processes, and you enabled paranoid warnings. (Note that",
                        "this may be legitimate and innocent usage; this warning is meaningless without further investigation.)"
                    );
                }
                LogWarningGroup(ModWarning.PatchesGame, LogLevel.Info, "Patched game code",
                    "These mods directly change the game code. They're more likely to cause errors or bugs in-game; if",
                    "your game has issues, try removing these first. Otherwise you can ignore this warning."
                );
                LogWarningGroup(ModWarning.UsesUnvalidatedUpdateTick, LogLevel.Info, "Bypassed safety checks",
                    "These mods bypass SMAPI's normal safety checks, so they're more likely to cause errors or save",
                    "corruption. If your game has issues, try removing these first."
                );
                LogWarningGroup(ModWarning.NoUpdateKeys, LogLevel.Debug, "No update keys",
                    "These mods have no update keys in their manifest. SMAPI may not notify you about updates for these",
                    "mods. Consider notifying the mod authors about this problem."
                );
                LogWarningGroup(ModWarning.UsesDynamic, LogLevel.Debug, "Not crossplatform",
                    "These mods use the 'dynamic' keyword, and won't work on Linux/Mac."
                );
            }
        }

        /// <summary>Load a mod's entry class.</summary>
        /// <param name="modAssembly">The mod assembly.</param>
        /// <param name="mod">The loaded instance.</param>
        /// <param name="error">The error indicating why loading failed (if applicable).</param>
        /// <returns>Returns whether the mod entry class was successfully loaded.</returns>
        private bool TryLoadModEntry(Assembly modAssembly, out Mod mod, out string error)
        {
            mod = null;

            // find type
            TypeInfo[] modEntries = modAssembly.DefinedTypes.Where(type => typeof(Mod).IsAssignableFrom(type) && !type.IsAbstract).Take(2).ToArray();
            if (modEntries.Length == 0)
            {
                error = $"its DLL has no '{nameof(Mod)}' subclass.";
                return false;
            }
            if (modEntries.Length > 1)
            {
                error = $"its DLL contains multiple '{nameof(Mod)}' subclasses.";
                return false;
            }

            // get implementation
            mod = (Mod)modAssembly.CreateInstance(modEntries[0].ToString());
            if (mod == null)
            {
                error = "its entry class couldn't be instantiated.";
                return false;
            }

            error = null;
            return true;
        }

        /// <summary>Reload translations for all mods.</summary>
        /// <param name="mods">The mods for which to reload translations.</param>
        private void ReloadTranslations(IEnumerable<IModMetadata> mods)
        {
            // core SMAPI translations
            {
                var translations = this.ReadTranslationFiles(Path.Combine(Constants.InternalFilesPath, "i18n"), out IList<string> errors);
                if (errors.Any() || !translations.Any())
                {
                    this.Monitor.Log("SMAPI couldn't load some core translations. You may need to reinstall SMAPI.", LogLevel.Warn);
                    foreach (string error in errors)
                        this.Monitor.Log($"  - {error}", LogLevel.Warn);
                }
                this.Translator.SetTranslations(translations);
            }

            // mod translations
            foreach (IModMetadata metadata in mods)
            {
                var translations = this.ReadTranslationFiles(Path.Combine(metadata.DirectoryPath, "i18n"), out IList<string> errors);
                if (errors.Any())
                {
                    metadata.LogAsMod("Mod couldn't load some translation files:", LogLevel.Warn);
                    foreach (string error in errors)
                        metadata.LogAsMod($"  - {error}", LogLevel.Warn);
                }
                metadata.Translations.SetTranslations(translations);
            }
        }

        /// <summary>Read translations from a directory containing JSON translation files.</summary>
        /// <param name="folderPath">The folder path to search.</param>
        /// <param name="errors">The errors indicating why translation files couldn't be parsed, indexed by translation filename.</param>
        private IDictionary<string, IDictionary<string, string>> ReadTranslationFiles(string folderPath, out IList<string> errors)
        {
            JsonHelper jsonHelper = this.Toolkit.JsonHelper;

            // read translation files
            var translations = new Dictionary<string, IDictionary<string, string>>();
            errors = new List<string>();
            DirectoryInfo translationsDir = new DirectoryInfo(folderPath);
            if (translationsDir.Exists)
            {
                foreach (FileInfo file in translationsDir.EnumerateFiles("*.json"))
                {
                    string locale = Path.GetFileNameWithoutExtension(file.Name.ToLower().Trim());
                    try
                    {
                        if (!jsonHelper.ReadJsonFileIfExists(file.FullName, out IDictionary<string, string> data))
                        {
                            errors.Add($"{file.Name} file couldn't be read"); // should never happen, since we're iterating files that exist
                            continue;
                        }

                        translations[locale] = data;
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"{file.Name} file couldn't be parsed: {ex.GetLogSummary()}");
                        continue;
                    }
                }
            }

            // validate translations
            foreach (string locale in translations.Keys.ToArray())
            {
                // handle duplicates
                HashSet<string> keys = new HashSet<string>(StringComparer.InvariantCultureIgnoreCase);
                HashSet<string> duplicateKeys = new HashSet<string>(StringComparer.InvariantCultureIgnoreCase);
                foreach (string key in translations[locale].Keys.ToArray())
                {
                    if (!keys.Add(key))
                    {
                        duplicateKeys.Add(key);
                        translations[locale].Remove(key);
                    }
                }
                if (duplicateKeys.Any())
                    errors.Add($"{locale}.json has duplicate translation keys: [{string.Join(", ", duplicateKeys)}]. Keys are case-insensitive.");
            }

            return translations;
        }

        /// <summary>The method called when the user submits a core SMAPI command in the console.</summary>
        /// <param name="name">The command name.</param>
        /// <param name="arguments">The command arguments.</param>
        private void HandleCommand(string name, string[] arguments)
        {
            switch (name)
            {
                case "help":
                    if (arguments.Any())
                    {
                        Command result = this.GameInstance.CommandManager.Get(arguments[0]);
                        if (result == null)
                            this.Monitor.Log("There's no command with that name.", LogLevel.Error);
                        else
                            this.Monitor.Log($"{result.Name}: {result.Documentation}{(result.Mod != null ? $"\n(Added by {result.Mod.DisplayName}.)" : "")}", LogLevel.Info);
                    }
                    else
                    {
                        string message = "The following commands are registered:\n";
                        IGrouping<string, string>[] groups = (from command in this.GameInstance.CommandManager.GetAll() orderby command.Mod?.DisplayName, command.Name group command.Name by command.Mod?.DisplayName).ToArray();
                        foreach (var group in groups)
                        {
                            string modName = group.Key ?? "SMAPI";
                            string[] commandNames = group.ToArray();
                            message += $"{modName}:\n  {string.Join("\n  ", commandNames)}\n\n";
                        }
                        message += "For more information about a command, type 'help command_name'.";

                        this.Monitor.Log(message, LogLevel.Info);
                    }
                    break;

                case "reload_i18n":
                    this.ReloadTranslations(this.ModRegistry.GetAll(contentPacks: false));
                    this.Monitor.Log("Reloaded translation files for all mods. This only affects new translations the mods fetch; if they cached some text, it may not be updated.", LogLevel.Info);
                    break;

                default:
                    throw new NotSupportedException($"Unrecognized core SMAPI command '{name}'.");
            }
        }

        /// <summary>Redirect messages logged directly to the console to the given monitor.</summary>
        /// <param name="gameMonitor">The monitor with which to log messages as the game.</param>
        /// <param name="message">The message to log.</param>
        private void HandleConsoleMessage(IMonitor gameMonitor, string message)
        {
            // detect exception
            LogLevel level = message.Contains("Exception") ? LogLevel.Error : LogLevel.Trace;

            // ignore suppressed message
            if (level != LogLevel.Error && this.SuppressConsolePatterns.Any(p => p.IsMatch(message)))
                return;

            // show friendly error if applicable
            foreach (ReplaceLogPattern entry in this.ReplaceConsolePatterns)
            {
                string newMessage = entry.Search.Replace(message, entry.Replacement);
                if (message != newMessage)
                {
                    gameMonitor.Log(newMessage, entry.LogLevel);
                    gameMonitor.Log(message, LogLevel.Trace);
                    return;
                }
            }

            // forward to monitor
            gameMonitor.Log(message, level);
        }

        /// <summary>Show a 'press any key to exit' message, and exit when they press a key.</summary>
        private void PressAnyKeyToExit()
        {
            this.Monitor.Log("Game has ended. Press any key to exit.", LogLevel.Info);
            this.PressAnyKeyToExit(showMessage: false);
        }

        /// <summary>Show a 'press any key to exit' message, and exit when they press a key.</summary>
        /// <param name="showMessage">Whether to print a 'press any key to exit' message to the console.</param>
        private void PressAnyKeyToExit(bool showMessage)
        {
            if (showMessage)
                Console.WriteLine("Game has ended. Press any key to exit.");
            Thread.Sleep(100);
            Console.ReadKey();
            Environment.Exit(0);
        }

        /// <summary>Get a monitor instance derived from SMAPI's current settings.</summary>
        /// <param name="name">The name of the module which will log messages with this instance.</param>
        private Monitor GetSecondaryMonitor(string name)
        {
            return new Monitor(name, this.ConsoleManager, this.LogFile, this.Settings.ConsoleColors, this.Settings.VerboseLogging)
            {
                WriteToConsole = this.Monitor.WriteToConsole,
                ShowTraceInConsole = this.Settings.DeveloperMode,
                ShowFullStampInConsole = this.Settings.DeveloperMode
            };
        }

        /// <summary>Get the absolute path to the next available log file.</summary>
        private string GetLogPath()
        {
            // default path
            {
                FileInfo defaultFile = new FileInfo(Path.Combine(Constants.LogDir, $"{Constants.LogFilename}.{Constants.LogExtension}"));
                if (!defaultFile.Exists)
                    return defaultFile.FullName;
            }

            // get first disambiguated path
            for (int i = 2; i < int.MaxValue; i++)
            {
                FileInfo file = new FileInfo(Path.Combine(Constants.LogDir, $"{Constants.LogFilename}.player-{i}.{Constants.LogExtension}"));
                if (!file.Exists)
                    return file.FullName;
            }

            // should never happen
            throw new InvalidOperationException("Could not find an available log path.");
        }

        /// <summary>Delete normal (non-crash) log files created by SMAPI.</summary>
        private void PurgeNormalLogs()
        {
            DirectoryInfo logsDir = new DirectoryInfo(Constants.LogDir);
            if (!logsDir.Exists)
                return;

            foreach (FileInfo logFile in logsDir.EnumerateFiles())
            {
                // skip non-SMAPI file
                if (!logFile.Name.StartsWith(Constants.LogNamePrefix, StringComparison.InvariantCultureIgnoreCase))
                    continue;

                // skip crash log
                if (logFile.FullName == Constants.FatalCrashLog)
                    continue;

                // delete file
                try
                {
                    FileUtilities.ForceDelete(logFile);
                }
                catch (IOException)
                {
                    // ignore file if it's in use
                }
            }
        }

        /// <summary>A console log pattern to replace with a different message.</summary>
        private class ReplaceLogPattern
        {
            /*********
            ** Accessors
            *********/
            /// <summary>The regex pattern matching the portion of the message to replace.</summary>
            public Regex Search { get; }

            /// <summary>The replacement string.</summary>
            public string Replacement { get; }

            /// <summary>The log level for the new message.</summary>
            public LogLevel LogLevel { get; }


            /*********
            ** Public methods
            *********/
            /// <summary>Construct an instance.</summary>
            /// <param name="search">The regex pattern matching the portion of the message to replace.</param>
            /// <param name="replacement">The replacement string.</param>
            /// <param name="logLevel">The log level for the new message.</param>
            public ReplaceLogPattern(Regex search, string replacement, LogLevel logLevel)
            {
                this.Search = search;
                this.Replacement = replacement;
                this.LogLevel = logLevel;
            }
        }
    }
}
