using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Netcode;
using StardewModdingAPI.Enums;
using StardewModdingAPI.Events;
using StardewModdingAPI.Framework.Content;
using StardewModdingAPI.Framework.Events;
using StardewModdingAPI.Framework.Input;
using StardewModdingAPI.Framework.Networking;
using StardewModdingAPI.Framework.PerformanceMonitoring;
using StardewModdingAPI.Framework.Reflection;
using StardewModdingAPI.Framework.Rendering;
using StardewModdingAPI.Framework.StateTracking.Comparers;
using StardewModdingAPI.Framework.StateTracking.Snapshots;
using StardewModdingAPI.Framework.Utilities;
using StardewModdingAPI.Toolkit.Serialization;
using StardewValley;
using StardewValley.BellsAndWhistles;
using StardewValley.Events;
using StardewValley.Locations;
using StardewValley.Menus;
using StardewValley.Tools;
using xTile.Dimensions;
using xTile.Layers;
using xTile.Tiles;

namespace StardewModdingAPI.Framework
{
    /// <summary>SMAPI's extension of the game's core <see cref="Game1"/>, used to inject events.</summary>
    internal class SGame : Game1
    {
        /*********
        ** Fields
        *********/
        /****
        ** SMAPI state
        ****/
        /// <summary>Encapsulates monitoring and logging for SMAPI.</summary>
        private readonly Monitor Monitor;

        /// <summary>Encapsulates monitoring and logging on the game's behalf.</summary>
        private readonly IMonitor MonitorForGame;

        /// <summary>Manages SMAPI events for mods.</summary>
        private readonly EventManager Events;

        /// <summary>Tracks the installed mods.</summary>
        private readonly ModRegistry ModRegistry;

        /// <summary>Manages deprecation warnings.</summary>
        private readonly DeprecationManager DeprecationManager;

        /// <summary>Tracks performance metrics.</summary>
        private readonly PerformanceMonitor PerformanceMonitor;

        /// <summary>The maximum number of consecutive attempts SMAPI should make to recover from a draw error.</summary>
        private readonly Countdown DrawCrashTimer = new Countdown(60); // 60 ticks = roughly one second

        /// <summary>The maximum number of consecutive attempts SMAPI should make to recover from an update error.</summary>
        private readonly Countdown UpdateCrashTimer = new Countdown(60); // 60 ticks = roughly one second

        /// <summary>The number of ticks until SMAPI should notify mods that the game has loaded.</summary>
        /// <remarks>Skipping a few frames ensures the game finishes initializing the world before mods try to change it.</remarks>
        private readonly Countdown AfterLoadTimer = new Countdown(5);

        /// <summary>Whether custom content was removed from the save data to avoid a crash.</summary>
        private bool IsSaveContentRemoved;

        /// <summary>Whether the game is saving and SMAPI has already raised <see cref="IGameLoopEvents.Saving"/>.</summary>
        private bool IsBetweenSaveEvents;

        /// <summary>Whether the game is creating the save file and SMAPI has already raised <see cref="IGameLoopEvents.SaveCreating"/>.</summary>
        private bool IsBetweenCreateEvents;

        /// <summary>A callback to invoke the first time *any* game content manager loads an asset.</summary>
        private readonly Action OnLoadingFirstAsset;

        /// <summary>A callback to invoke after the game finishes initializing.</summary>
        private readonly Action OnGameInitialized;

        /// <summary>A callback to invoke when the game exits.</summary>
        private readonly Action OnGameExiting;

        /// <summary>Simplifies access to private game code.</summary>
        private readonly Reflector Reflection;

        /// <summary>Encapsulates access to SMAPI core translations.</summary>
        private readonly Translator Translator;

        /// <summary>Propagates notification that SMAPI should exit.</summary>
        private readonly CancellationTokenSource CancellationToken;

        /****
        ** Game state
        ****/
        /// <summary>Monitors the entire game state for changes.</summary>
        private WatcherCore Watchers;

        /// <summary>A snapshot of the current <see cref="Watchers"/> state.</summary>
        private readonly WatcherSnapshot WatcherSnapshot = new WatcherSnapshot();

        /// <summary>Whether post-game-startup initialization has been performed.</summary>
        private bool IsInitialized;

        /// <summary>Whether the next content manager requested by the game will be for <see cref="Game1.content"/>.</summary>
        private bool NextContentManagerIsMain;


        /*********
        ** Accessors
        *********/
        /// <summary>Static state to use while <see cref="Game1"/> is initializing, which happens before the <see cref="SGame"/> constructor runs.</summary>
        internal static SGameConstructorHack ConstructorHack { get; set; }

        /// <summary>The number of update ticks which have already executed. This is similar to <see cref="Game1.ticks"/>, but incremented more consistently for every tick.</summary>
        internal static uint TicksElapsed { get; private set; }

        /// <summary>SMAPI's content manager.</summary>
        public ContentCoordinator ContentCore { get; private set; }

        /// <summary>Manages console commands.</summary>
        public CommandManager CommandManager { get; } = new CommandManager();

        /// <summary>Manages input visible to the game.</summary>
        public SInputState Input => (SInputState)Game1.input;

        /// <summary>The game's core multiplayer utility.</summary>
        public SMultiplayer Multiplayer => (SMultiplayer)Game1.multiplayer;

        /// <summary>A list of queued commands to execute.</summary>
        /// <remarks>This property must be threadsafe, since it's accessed from a separate console input thread.</remarks>
        public ConcurrentQueue<string> CommandQueue { get; } = new ConcurrentQueue<string>();

        /// <summary>Asset interceptors added or removed since the last tick.</summary>
        private readonly List<AssetInterceptorChange> ReloadAssetInterceptorsQueue = new List<AssetInterceptorChange>();


        /*********
        ** Protected methods
        *********/
        /// <summary>Construct an instance.</summary>
        /// <param name="monitor">Encapsulates monitoring and logging for SMAPI.</param>
        /// <param name="monitorForGame">Encapsulates monitoring and logging on the game's behalf.</param>
        /// <param name="reflection">Simplifies access to private game code.</param>
        /// <param name="translator">Encapsulates access to arbitrary translations.</param>
        /// <param name="eventManager">Manages SMAPI events for mods.</param>
        /// <param name="jsonHelper">Encapsulates SMAPI's JSON file parsing.</param>
        /// <param name="modRegistry">Tracks the installed mods.</param>
        /// <param name="deprecationManager">Manages deprecation warnings.</param>
        /// <param name="performanceMonitor">Tracks performance metrics.</param>
        /// <param name="onGameInitialized">A callback to invoke after the game finishes initializing.</param>
        /// <param name="onGameExiting">A callback to invoke when the game exits.</param>
        /// <param name="cancellationToken">Propagates notification that SMAPI should exit.</param>
        /// <param name="logNetworkTraffic">Whether to log network traffic.</param>
        internal SGame(Monitor monitor, IMonitor monitorForGame, Reflector reflection, Translator translator, EventManager eventManager, JsonHelper jsonHelper, ModRegistry modRegistry, DeprecationManager deprecationManager, PerformanceMonitor performanceMonitor, Action onGameInitialized, Action onGameExiting, CancellationTokenSource cancellationToken, bool logNetworkTraffic)
        {
            this.OnLoadingFirstAsset = SGame.ConstructorHack.OnLoadingFirstAsset;
            SGame.ConstructorHack = null;

            // check expectations
            if (this.ContentCore == null)
                throw new InvalidOperationException($"The game didn't initialize its first content manager before SMAPI's {nameof(SGame)} constructor. This indicates an incompatible lifecycle change.");

            // init XNA
            Game1.graphics.GraphicsProfile = GraphicsProfile.HiDef;

            // init SMAPI
            this.Monitor = monitor;
            this.MonitorForGame = monitorForGame;
            this.Events = eventManager;
            this.ModRegistry = modRegistry;
            this.Reflection = reflection;
            this.Translator = translator;
            this.DeprecationManager = deprecationManager;
            this.PerformanceMonitor = performanceMonitor;
            this.OnGameInitialized = onGameInitialized;
            this.OnGameExiting = onGameExiting;
            Game1.input = new SInputState();
            Game1.multiplayer = new SMultiplayer(monitor, eventManager, jsonHelper, modRegistry, reflection, this.OnModMessageReceived, logNetworkTraffic);
            Game1.hooks = new SModHooks(this.OnNewDayAfterFade);
            this.CancellationToken = cancellationToken;

            // init observables
            Game1.locations = new ObservableCollection<GameLocation>();
        }

        /// <summary>Load content when the game is launched.</summary>
        protected override void LoadContent()
        {
            base.LoadContent();
            Game1.mapDisplayDevice = new SDisplayDevice(Game1.content, this.GraphicsDevice);
        }

        /// <summary>Initialize just before the game's first update tick.</summary>
        private void InitializeAfterGameStarted()
        {
            // set initial state
            this.Input.TrueUpdate();

            // init watchers
            this.Watchers = new WatcherCore(this.Input);

            // raise callback
            this.OnGameInitialized();
        }

        /// <summary>Perform cleanup logic when the game exits.</summary>
        /// <param name="sender">The event sender.</param>
        /// <param name="args">The event args.</param>
        /// <remarks>This overrides the logic in <see cref="Game1.exitEvent"/> to let SMAPI clean up before exit.</remarks>
        protected override void OnExiting(object sender, EventArgs args)
        {
            Game1.multiplayer.Disconnect(StardewValley.Multiplayer.DisconnectType.ClosedGame);
            this.OnGameExiting?.Invoke();
        }

        /// <summary>A callback invoked before <see cref="Game1.newDayAfterFade"/> runs.</summary>
        protected void OnNewDayAfterFade()
        {
            this.Events.DayEnding.RaiseEmpty();
        }

        /// <summary>A callback invoked when a mod message is received.</summary>
        /// <param name="message">The message to deliver to applicable mods.</param>
        private void OnModMessageReceived(ModMessageModel message)
        {
            // get mod IDs to notify
            HashSet<string> modIDs = new HashSet<string>(message.ToModIDs ?? this.ModRegistry.GetAll().Select(p => p.Manifest.UniqueID), StringComparer.InvariantCultureIgnoreCase);
            if (message.FromPlayerID == Game1.player?.UniqueMultiplayerID)
                modIDs.Remove(message.FromModID); // don't send a broadcast back to the sender

            // raise events
            this.Events.ModMessageReceived.RaiseForMods(new ModMessageReceivedEventArgs(message), mod => mod != null && modIDs.Contains(mod.Manifest.UniqueID));
        }

        /// <summary>A callback invoked when custom content is removed from the save data to avoid a crash.</summary>
        internal void OnSaveContentRemoved()
        {
            this.IsSaveContentRemoved = true;
        }

        /// <summary>A callback invoked when the game's low-level load stage changes.</summary>
        /// <param name="newStage">The new load stage.</param>
        internal void OnLoadStageChanged(LoadStage newStage)
        {
            // nothing to do
            if (newStage == Context.LoadStage)
                return;

            // update data
            LoadStage oldStage = Context.LoadStage;
            Context.LoadStage = newStage;
            this.Monitor.VerboseLog($"Context: load stage changed to {newStage}");
            if (newStage == LoadStage.None)
            {
                this.Monitor.Log("Context: returned to title", LogLevel.Trace);
                this.OnReturnedToTitle();
            }

            // raise events
            this.Events.LoadStageChanged.Raise(new LoadStageChangedEventArgs(oldStage, newStage));
            if (newStage == LoadStage.None)
                this.Events.ReturnedToTitle.RaiseEmpty();
        }

        /// <summary>A callback invoked when a mod adds or removes an asset interceptor.</summary>
        /// <param name="mod">The mod which added or removed interceptors.</param>
        /// <param name="added">The added interceptors.</param>
        /// <param name="removed">The removed interceptors.</param>
        internal void OnAssetInterceptorsChanged(IModMetadata mod, IEnumerable added, IEnumerable removed)
        {
            if (added != null)
            {
                foreach (object instance in added)
                    this.ReloadAssetInterceptorsQueue.Add(new AssetInterceptorChange(mod, instance, wasAdded: true));
            }
            if (removed != null)
            {
                foreach (object instance in removed)
                    this.ReloadAssetInterceptorsQueue.Add(new AssetInterceptorChange(mod, instance, wasAdded: false));
            }
        }

        /// <summary>Perform cleanup when the game returns to the title screen.</summary>
        private void OnReturnedToTitle()
        {
            this.Multiplayer.CleanupOnMultiplayerExit();

            if (!(Game1.mapDisplayDevice is SDisplayDevice))
                Game1.mapDisplayDevice = new SDisplayDevice(Game1.content, this.GraphicsDevice);
        }

        /// <summary>Constructor a content manager to read XNB files.</summary>
        /// <param name="serviceProvider">The service provider to use to locate services.</param>
        /// <param name="rootDirectory">The root directory to search for content.</param>
        protected override LocalizedContentManager CreateContentManager(IServiceProvider serviceProvider, string rootDirectory)
        {
            // Game1._temporaryContent initializing from SGame constructor
            // NOTE: this method is called before the SGame constructor runs. Don't depend on anything being initialized at this point.
            if (this.ContentCore == null)
            {
                this.ContentCore = new ContentCoordinator(serviceProvider, rootDirectory, Thread.CurrentThread.CurrentUICulture, SGame.ConstructorHack.Monitor, SGame.ConstructorHack.Reflection, SGame.ConstructorHack.JsonHelper, this.OnLoadingFirstAsset ?? SGame.ConstructorHack?.OnLoadingFirstAsset);
                this.NextContentManagerIsMain = true;
                return this.ContentCore.CreateGameContentManager("Game1._temporaryContent");
            }

            // Game1.content initializing from LoadContent
            if (this.NextContentManagerIsMain)
            {
                this.NextContentManagerIsMain = false;
                return this.ContentCore.MainContentManager;
            }

            // any other content manager
            return this.ContentCore.CreateGameContentManager("(generated)");
        }

        /// <summary>The method called when the game is updating its state. This happens roughly 60 times per second.</summary>
        /// <param name="gameTime">A snapshot of the game timing state.</param>
        protected override void Update(GameTime gameTime)
        {
            var events = this.Events;

            try
            {
                this.DeprecationManager.PrintQueued();
                this.PerformanceMonitor.PrintQueuedAlerts();

                /*********
                ** First-tick initialization
                *********/
                if (!this.IsInitialized)
                {
                    this.IsInitialized = true;
                    this.InitializeAfterGameStarted();
                }

                /*********
                ** Update input
                *********/
                // This should *always* run, even when suppressing mod events, since the game uses
                // this too. For example, doing this after mod event suppression would prevent the
                // user from doing anything on the overnight shipping screen.
                SInputState inputState = this.Input;
                if (this.IsActive)
                    inputState.TrueUpdate();

                /*********
                ** Special cases
                *********/
                // Abort if SMAPI is exiting.
                if (this.CancellationToken.IsCancellationRequested)
                {
                    this.Monitor.Log("SMAPI shutting down: aborting update.", LogLevel.Trace);
                    return;
                }

                // Run async tasks synchronously to avoid issues due to mod events triggering
                // concurrently with game code.
                bool saveParsed = false;
                if (Game1.currentLoader != null)
                {
                    this.Monitor.Log("Game loader synchronizing...", LogLevel.Trace);
                    while (Game1.currentLoader?.MoveNext() == true)
                    {
                        // raise load stage changed
                        switch (Game1.currentLoader.Current)
                        {
                            case 20 when (!saveParsed && SaveGame.loaded != null):
                                saveParsed = true;
                                this.OnLoadStageChanged(LoadStage.SaveParsed);
                                break;

                            case 36:
                                this.OnLoadStageChanged(LoadStage.SaveLoadedBasicInfo);
                                break;

                            case 50:
                                this.OnLoadStageChanged(LoadStage.SaveLoadedLocations);
                                break;

                            default:
                                if (Game1.gameMode == Game1.playingGameMode)
                                    this.OnLoadStageChanged(LoadStage.Preloaded);
                                break;
                        }
                    }

                    Game1.currentLoader = null;
                    this.Monitor.Log("Game loader done.", LogLevel.Trace);
                }
                if (Game1._newDayTask?.Status == TaskStatus.Created)
                {
                    this.Monitor.Log("New day task synchronizing...", LogLevel.Trace);
                    Game1._newDayTask.RunSynchronously();
                    this.Monitor.Log("New day task done.", LogLevel.Trace);
                }

                // While a background task is in progress, the game may make changes to the game
                // state while mods are running their code. This is risky, because data changes can
                // conflict (e.g. collection changed during enumeration errors) and data may change
                // unexpectedly from one mod instruction to the next.
                //
                // Therefore we can just run Game1.Update here without raising any SMAPI events. There's
                // a small chance that the task will finish after we defer but before the game checks,
                // which means technically events should be raised, but the effects of missing one
                // update tick are negligible and not worth the complications of bypassing Game1.Update.
                if (Game1._newDayTask != null || Game1.gameMode == Game1.loadingMode)
                {
                    events.UnvalidatedUpdateTicking.RaiseEmpty();
                    SGame.TicksElapsed++;
                    base.Update(gameTime);
                    events.UnvalidatedUpdateTicked.RaiseEmpty();
                    return;
                }

                // Raise minimal events while saving.
                // While the game is writing to the save file in the background, mods can unexpectedly
                // fail since they don't have exclusive access to resources (e.g. collection changed
                // during enumeration errors). To avoid problems, events are not invoked while a save
                // is in progress. It's safe to raise SaveEvents.BeforeSave as soon as the menu is
                // opened (since the save hasn't started yet), but all other events should be suppressed.
                if (Context.IsSaving)
                {
                    // raise before-create
                    if (!Context.IsWorldReady && !this.IsBetweenCreateEvents)
                    {
                        this.IsBetweenCreateEvents = true;
                        this.Monitor.Log("Context: before save creation.", LogLevel.Trace);
                        events.SaveCreating.RaiseEmpty();
                    }

                    // raise before-save
                    if (Context.IsWorldReady && !this.IsBetweenSaveEvents)
                    {
                        this.IsBetweenSaveEvents = true;
                        this.Monitor.Log("Context: before save.", LogLevel.Trace);
                        events.Saving.RaiseEmpty();
                    }

                    // suppress non-save events
                    events.UnvalidatedUpdateTicking.RaiseEmpty();
                    SGame.TicksElapsed++;
                    base.Update(gameTime);
                    events.UnvalidatedUpdateTicked.RaiseEmpty();
                    return;
                }

                /*********
                ** Reload assets when interceptors are added/removed
                *********/
                if (this.ReloadAssetInterceptorsQueue.Any())
                {
                    // get unique interceptors
                    AssetInterceptorChange[] interceptors = this.ReloadAssetInterceptorsQueue
                        .GroupBy(p => p.Instance, new ObjectReferenceComparer<object>())
                        .Select(p => p.First())
                        .ToArray();
                    this.ReloadAssetInterceptorsQueue.Clear();

                    // log summary
                    this.Monitor.Log("Invalidating cached assets for new editors & loaders...");
                    this.Monitor.Log(
                        "   changed: "
                        + string.Join(", ",
                            interceptors
                                .GroupBy(p => p.Mod)
                                .OrderBy(p => p.Key.DisplayName)
                                .Select(modGroup =>
                                    $"{modGroup.Key.DisplayName} ("
                                    + string.Join(", ", modGroup.GroupBy(p => p.WasAdded).ToDictionary(p => p.Key, p => p.Count()).Select(p => $"{(p.Key ? "added" : "removed")} {p.Value}"))
                                    + ")"
                            )
                        )
                    );

                    // reload affected assets
                    this.ContentCore.InvalidateCache(asset => interceptors.Any(p => p.CanIntercept(asset)));
                }

                /*********
                ** Execute commands
                *********/
                while (this.CommandQueue.TryDequeue(out string rawInput))
                {
                    // parse command
                    string name;
                    string[] args;
                    Command command;
                    try
                    {
                        if (!this.CommandManager.TryParse(rawInput, out name, out args, out command))
                        {
                            this.Monitor.Log("Unknown command; type 'help' for a list of available commands.", LogLevel.Error);
                            continue;
                        }
                    }
                    catch (Exception ex)
                    {
                        this.Monitor.Log($"Failed parsing that command:\n{ex.GetLogSummary()}", LogLevel.Error);
                        continue;
                    }

                    // execute command
                    try
                    {
                        command.Callback.Invoke(name, args);
                    }
                    catch (Exception ex)
                    {
                        if (command.Mod != null)
                            command.Mod.LogAsMod($"Mod failed handling that command:\n{ex.GetLogSummary()}", LogLevel.Error);
                        else
                            this.Monitor.Log($"Failed handling that command:\n{ex.GetLogSummary()}", LogLevel.Error);
                    }
                }

                /*********
                ** Update context
                *********/
                bool wasWorldReady = Context.IsWorldReady;
                if ((Context.IsWorldReady && !Context.IsSaveLoaded) || Game1.exitToTitle)
                {
                    Context.IsWorldReady = false;
                    this.AfterLoadTimer.Reset();
                }
                else if (Context.IsSaveLoaded && this.AfterLoadTimer.Current > 0 && Game1.currentLocation != null)
                {
                    if (Game1.dayOfMonth != 0) // wait until new-game intro finishes (world not fully initialized yet)
                        this.AfterLoadTimer.Decrement();
                    Context.IsWorldReady = this.AfterLoadTimer.Current == 0;
                }

                /*********
                ** Update watchers
                **   (Watchers need to be updated, checked, and reset in one go so we can detect any changes mods make in event handlers.)
                *********/
                this.Watchers.Update();
                this.WatcherSnapshot.Update(this.Watchers);
                this.Watchers.Reset();
                WatcherSnapshot state = this.WatcherSnapshot;

                /*********
                ** Display in-game warnings
                *********/
                // save content removed
                if (this.IsSaveContentRemoved && Context.IsWorldReady)
                {
                    this.IsSaveContentRemoved = false;
                    Game1.addHUDMessage(new HUDMessage(this.Translator.Get("warn.invalid-content-removed"), HUDMessage.error_type));
                }

                /*********
                ** Pre-update events
                *********/
                {
                    /*********
                    ** Save created/loaded events
                    *********/
                    if (this.IsBetweenCreateEvents)
                    {
                        // raise after-create
                        this.IsBetweenCreateEvents = false;
                        this.Monitor.Log($"Context: after save creation, starting {Game1.currentSeason} {Game1.dayOfMonth} Y{Game1.year}.", LogLevel.Trace);
                        this.OnLoadStageChanged(LoadStage.CreatedSaveFile);
                        events.SaveCreated.RaiseEmpty();
                    }
                    if (this.IsBetweenSaveEvents)
                    {
                        // raise after-save
                        this.IsBetweenSaveEvents = false;
                        this.Monitor.Log($"Context: after save, starting {Game1.currentSeason} {Game1.dayOfMonth} Y{Game1.year}.", LogLevel.Trace);
                        events.Saved.RaiseEmpty();
                        events.DayStarted.RaiseEmpty();
                    }

                    /*********
                    ** Locale changed events
                    *********/
                    if (state.Locale.IsChanged)
                        this.Monitor.Log($"Context: locale set to {state.Locale.New}.", LogLevel.Trace);

                    /*********
                    ** Load / return-to-title events
                    *********/
                    if (wasWorldReady && !Context.IsWorldReady)
                        this.OnLoadStageChanged(LoadStage.None);
                    else if (Context.IsWorldReady && Context.LoadStage != LoadStage.Ready)
                    {
                        // print context
                        string context = $"Context: loaded save '{Constants.SaveFolderName}', starting {Game1.currentSeason} {Game1.dayOfMonth} Y{Game1.year}, locale set to {this.ContentCore.Language}.";
                        if (Context.IsMultiplayer)
                        {
                            int onlineCount = Game1.getOnlineFarmers().Count();
                            context += $" {(Context.IsMainPlayer ? "Main player" : "Farmhand")} with {onlineCount} {(onlineCount == 1 ? "player" : "players")} online.";
                        }
                        else
                            context += " Single-player.";
                        this.Monitor.Log(context, LogLevel.Trace);

                        // raise events
                        this.OnLoadStageChanged(LoadStage.Ready);
                        events.SaveLoaded.RaiseEmpty();
                        events.DayStarted.RaiseEmpty();
                    }

                    /*********
                    ** Window events
                    *********/
                    // Here we depend on the game's viewport instead of listening to the Window.Resize
                    // event because we need to notify mods after the game handles the resize, so the
                    // game's metadata (like Game1.viewport) are updated. That's a bit complicated
                    // since the game adds & removes its own handler on the fly.
                    if (state.WindowSize.IsChanged)
                    {
                        if (this.Monitor.IsVerbose)
                            this.Monitor.Log($"Events: window size changed to {state.WindowSize.New}.", LogLevel.Trace);

                        events.WindowResized.Raise(new WindowResizedEventArgs(state.WindowSize.Old, state.WindowSize.New));
                    }

                    /*********
                    ** Input events (if window has focus)
                    *********/
                    if (this.IsActive)
                    {
                        // raise events
                        bool isChatInput = Game1.IsChatting || (Context.IsMultiplayer && Context.IsWorldReady && Game1.activeClickableMenu == null && Game1.currentMinigame == null && inputState.IsAnyDown(Game1.options.chatButton));
                        if (!isChatInput)
                        {
                            ICursorPosition cursor = this.Input.CursorPosition;

                            // raise cursor moved event
                            if (state.Cursor.IsChanged)
                                events.CursorMoved.Raise(new CursorMovedEventArgs(state.Cursor.Old, state.Cursor.New));

                            // raise mouse wheel scrolled
                            if (state.MouseWheelScroll.IsChanged)
                            {
                                if (this.Monitor.IsVerbose)
                                    this.Monitor.Log($"Events: mouse wheel scrolled to {state.MouseWheelScroll.New}.", LogLevel.Trace);
                                events.MouseWheelScrolled.Raise(new MouseWheelScrolledEventArgs(cursor, state.MouseWheelScroll.Old, state.MouseWheelScroll.New));
                            }

                            // raise input button events
                            foreach (var pair in inputState.LastButtonStates)
                            {
                                SButton button = pair.Key;
                                SButtonState status = pair.Value;

                                if (status == SButtonState.Pressed)
                                {
                                    if (this.Monitor.IsVerbose)
                                        this.Monitor.Log($"Events: button {button} pressed.", LogLevel.Trace);

                                    events.ButtonPressed.Raise(new ButtonPressedEventArgs(button, cursor, inputState));
                                }
                                else if (status == SButtonState.Released)
                                {
                                    if (this.Monitor.IsVerbose)
                                        this.Monitor.Log($"Events: button {button} released.", LogLevel.Trace);

                                    events.ButtonReleased.Raise(new ButtonReleasedEventArgs(button, cursor, inputState));
                                }
                            }
                        }
                    }

                    /*********
                    ** Menu events
                    *********/
                    if (state.ActiveMenu.IsChanged)
                    {
                        if (this.Monitor.IsVerbose)
                            this.Monitor.Log($"Context: menu changed from {state.ActiveMenu.Old?.GetType().FullName ?? "none"} to {state.ActiveMenu.New?.GetType().FullName ?? "none"}.", LogLevel.Trace);

                        // raise menu events
                        events.MenuChanged.Raise(new MenuChangedEventArgs(state.ActiveMenu.Old, state.ActiveMenu.New));
                    }

                    /*********
                    ** World & player events
                    *********/
                    if (Context.IsWorldReady)
                    {
                        bool raiseWorldEvents = !state.SaveID.IsChanged; // don't report changes from unloaded => loaded

                        // location list changes
                        if (state.Locations.LocationList.IsChanged && (events.LocationListChanged.HasListeners() || this.Monitor.IsVerbose))
                        {
                            var added = state.Locations.LocationList.Added.ToArray();
                            var removed = state.Locations.LocationList.Removed.ToArray();

                            if (this.Monitor.IsVerbose)
                            {
                                string addedText = added.Any() ? string.Join(", ", added.Select(p => p.Name)) : "none";
                                string removedText = removed.Any() ? string.Join(", ", removed.Select(p => p.Name)) : "none";
                                this.Monitor.Log($"Context: location list changed (added {addedText}; removed {removedText}).", LogLevel.Trace);
                            }

                            events.LocationListChanged.Raise(new LocationListChangedEventArgs(added, removed));
                        }

                        // raise location contents changed
                        if (raiseWorldEvents)
                        {
                            foreach (LocationSnapshot locState in state.Locations.Locations)
                            {
                                var location = locState.Location;

                                // buildings changed
                                if (locState.Buildings.IsChanged)
                                    events.BuildingListChanged.Raise(new BuildingListChangedEventArgs(location, locState.Buildings.Added, locState.Buildings.Removed));

                                // debris changed
                                if (locState.Debris.IsChanged)
                                    events.DebrisListChanged.Raise(new DebrisListChangedEventArgs(location, locState.Debris.Added, locState.Debris.Removed));

                                // large terrain features changed
                                if (locState.LargeTerrainFeatures.IsChanged)
                                    events.LargeTerrainFeatureListChanged.Raise(new LargeTerrainFeatureListChangedEventArgs(location, locState.LargeTerrainFeatures.Added, locState.LargeTerrainFeatures.Removed));

                                // NPCs changed
                                if (locState.Npcs.IsChanged)
                                    events.NpcListChanged.Raise(new NpcListChangedEventArgs(location, locState.Npcs.Added, locState.Npcs.Removed));

                                // objects changed
                                if (locState.Objects.IsChanged)
                                    events.ObjectListChanged.Raise(new ObjectListChangedEventArgs(location, locState.Objects.Added, locState.Objects.Removed));

                                // chest items changed
                                if (events.ChestInventoryChanged.HasListeners())
                                {
                                    foreach (var pair in locState.ChestItems)
                                    {
                                        SnapshotItemListDiff diff = pair.Value;
                                        events.ChestInventoryChanged.Raise(new ChestInventoryChangedEventArgs(pair.Key, location, added: diff.Added, removed: diff.Removed, quantityChanged: diff.QuantityChanged));
                                    }
                                }

                                // terrain features changed
                                if (locState.TerrainFeatures.IsChanged)
                                    events.TerrainFeatureListChanged.Raise(new TerrainFeatureListChangedEventArgs(location, locState.TerrainFeatures.Added, locState.TerrainFeatures.Removed));
                            }
                        }

                        // raise time changed
                        if (raiseWorldEvents && state.Time.IsChanged)
                            events.TimeChanged.Raise(new TimeChangedEventArgs(state.Time.Old, state.Time.New));

                        // raise player events
                        if (raiseWorldEvents)
                        {
                            PlayerSnapshot playerState = state.CurrentPlayer;
                            Farmer player = playerState.Player;

                            // raise current location changed
                            if (playerState.Location.IsChanged)
                            {
                                if (this.Monitor.IsVerbose)
                                    this.Monitor.Log($"Context: set location to {playerState.Location.New}.", LogLevel.Trace);

                                events.Warped.Raise(new WarpedEventArgs(player, playerState.Location.Old, playerState.Location.New));
                            }

                            // raise player leveled up a skill
                            foreach (var pair in playerState.Skills)
                            {
                                if (!pair.Value.IsChanged)
                                    continue;

                                if (this.Monitor.IsVerbose)
                                    this.Monitor.Log($"Events: player skill '{pair.Key}' changed from {pair.Value.Old} to {pair.Value.New}.", LogLevel.Trace);

                                events.LevelChanged.Raise(new LevelChangedEventArgs(player, pair.Key, pair.Value.Old, pair.Value.New));
                            }

                            // raise player inventory changed
                            if (playerState.Inventory.IsChanged)
                            {
                                var inventory = playerState.Inventory;

                                if (this.Monitor.IsVerbose)
                                    this.Monitor.Log("Events: player inventory changed.", LogLevel.Trace);
                                events.InventoryChanged.Raise(new InventoryChangedEventArgs(player, added: inventory.Added, removed: inventory.Removed, quantityChanged: inventory.QuantityChanged));
                            }
                        }
                    }

                    /*********
                    ** Game update
                    *********/
                    // game launched
                    bool isFirstTick = SGame.TicksElapsed == 0;
                    if (isFirstTick)
                    {
                        Context.IsGameLaunched = true;
                        events.GameLaunched.Raise(new GameLaunchedEventArgs());
                    }

                    // preloaded
                    if (Context.IsSaveLoaded && Context.LoadStage != LoadStage.Loaded && Context.LoadStage != LoadStage.Ready && Game1.dayOfMonth != 0)
                        this.OnLoadStageChanged(LoadStage.Loaded);
                }

                /*********
                ** Game update tick
                *********/
                {
                    bool isOneSecond = SGame.TicksElapsed % 60 == 0;
                    events.UnvalidatedUpdateTicking.RaiseEmpty();
                    events.UpdateTicking.RaiseEmpty();
                    if (isOneSecond)
                        events.OneSecondUpdateTicking.RaiseEmpty();
                    try
                    {
                        this.Input.ApplyOverrides(); // if mods added any new overrides since the update, process them now
                        SGame.TicksElapsed++;
                        base.Update(gameTime);
                    }
                    catch (Exception ex)
                    {
                        this.MonitorForGame.Log($"An error occured in the base update loop: {ex.GetLogSummary()}", LogLevel.Error);
                    }

                    events.UnvalidatedUpdateTicked.RaiseEmpty();
                    events.UpdateTicked.RaiseEmpty();
                    if (isOneSecond)
                        events.OneSecondUpdateTicked.RaiseEmpty();
                }

                /*********
                ** Update events
                *********/
                this.UpdateCrashTimer.Reset();
            }
            catch (Exception ex)
            {
                // log error
                this.Monitor.Log($"An error occured in the overridden update loop: {ex.GetLogSummary()}", LogLevel.Error);

                // exit if irrecoverable
                if (!this.UpdateCrashTimer.Decrement())
                    this.ExitGameImmediately("The game crashed when updating, and SMAPI was unable to recover the game.");
            }
        }

        /// <summary>The method called to draw everything to the screen.</summary>
        /// <param name="gameTime">A snapshot of the game timing state.</param>
        /// <param name="target_screen">The render target, if any.</param>
        [SuppressMessage("ReSharper", "InconsistentNaming", Justification = "copied from game code as-is")]
        protected override void _draw(GameTime gameTime, RenderTarget2D target_screen)
        {
            Context.IsInDrawLoop = true;
            try
            {
                this.DrawImpl(gameTime, target_screen);
                this.DrawCrashTimer.Reset();
            }
            catch (Exception ex)
            {
                // log error
                this.Monitor.Log($"An error occured in the overridden draw loop: {ex.GetLogSummary()}", LogLevel.Error);

                // exit if irrecoverable
                if (!this.DrawCrashTimer.Decrement())
                {
                    this.ExitGameImmediately("The game crashed when drawing, and SMAPI was unable to recover the game.");
                    return;
                }

                // recover sprite batch
                try
                {
                    if (Game1.spriteBatch.IsOpen(this.Reflection))
                    {
                        this.Monitor.Log("Recovering sprite batch from error...", LogLevel.Trace);
                        Game1.spriteBatch.End();
                    }
                }
                catch (Exception innerEx)
                {
                    this.Monitor.Log($"Could not recover sprite batch state: {innerEx.GetLogSummary()}", LogLevel.Error);
                }
            }
            Context.IsInDrawLoop = false;
        }

        /// <summary>Replicate the game's draw logic with some changes for SMAPI.</summary>
        /// <param name="gameTime">A snapshot of the game timing state.</param>
        /// <param name="target_screen">The render target, if any.</param>
        /// <remarks>This implementation is identical to <see cref="Game1.Draw"/>, except for try..catch around menu draw code, private field references replaced by wrappers, and added events.</remarks>
        [SuppressMessage("ReSharper", "CompareOfFloatsByEqualityOperator", Justification = "copied from game code as-is")]
        [SuppressMessage("ReSharper", "InconsistentNaming", Justification = "copied from game code as-is")]
        [SuppressMessage("ReSharper", "LocalVariableHidesMember", Justification = "copied from game code as-is")]
        [SuppressMessage("ReSharper", "PossibleLossOfFraction", Justification = "copied from game code as-is")]
        [SuppressMessage("ReSharper", "RedundantArgumentDefaultValue", Justification = "copied from game code as-is")]
        [SuppressMessage("ReSharper", "RedundantCast", Justification = "copied from game code as-is")]
        [SuppressMessage("ReSharper", "RedundantExplicitNullableCreation", Justification = "copied from game code as-is")]
        [SuppressMessage("ReSharper", "RedundantTypeArgumentsOfMethod", Justification = "copied from game code as-is")]
        [SuppressMessage("SMAPI.CommonErrors", "AvoidNetField", Justification = "copied from game code as-is")]
        [SuppressMessage("SMAPI.CommonErrors", "AvoidImplicitNetFieldCast", Justification = "copied from game code as-is")]
        private void DrawImpl(GameTime gameTime, RenderTarget2D target_screen)
        {
            var events = this.Events;

            Game1.showingHealthBar = false;
            if (Game1._newDayTask != null)
            {
                this.GraphicsDevice.Clear(Game1.bgColor);
            }
            else
            {
                if (target_screen != null)
                    this.GraphicsDevice.SetRenderTarget(target_screen);
                if (this.IsSaving)
                {
                    this.GraphicsDevice.Clear(Game1.bgColor);
                    IClickableMenu activeClickableMenu = Game1.activeClickableMenu;
                    if (activeClickableMenu != null)
                    {
                        Game1.spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp, (DepthStencilState)null, (RasterizerState)null);
                        events.Rendering.RaiseEmpty();
                        try
                        {
                            events.RenderingActiveMenu.RaiseEmpty();
                            activeClickableMenu.draw(Game1.spriteBatch);
                            events.RenderedActiveMenu.RaiseEmpty();
                        }
                        catch (Exception ex)
                        {
                            this.Monitor.Log($"The {activeClickableMenu.GetType().FullName} menu crashed while drawing itself during save. SMAPI will force it to exit to avoid crashing the game.\n{ex.GetLogSummary()}", LogLevel.Error);
                            activeClickableMenu.exitThisMenu();
                        }
                        events.Rendered.RaiseEmpty();
                        Game1.spriteBatch.End();
                    }
                    if (Game1.overlayMenu != null)
                    {
                        Game1.spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp, (DepthStencilState)null, (RasterizerState)null);
                        Game1.overlayMenu.draw(Game1.spriteBatch);
                        Game1.spriteBatch.End();
                    }
                    this.renderScreenBuffer(target_screen);
                }
                else
                {
                    this.GraphicsDevice.Clear(Game1.bgColor);
                    if (Game1.activeClickableMenu != null && Game1.options.showMenuBackground && (Game1.activeClickableMenu.showWithoutTransparencyIfOptionIsSet() && !this.takingMapScreenshot))
                    {
                        Game1.spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp, (DepthStencilState)null, (RasterizerState)null);

                        events.Rendering.RaiseEmpty();
                        try
                        {
                            Game1.activeClickableMenu.drawBackground(Game1.spriteBatch);
                            events.RenderingActiveMenu.RaiseEmpty();
                            Game1.activeClickableMenu.draw(Game1.spriteBatch);
                            events.RenderedActiveMenu.RaiseEmpty();
                        }
                        catch (Exception ex)
                        {
                            this.Monitor.Log($"The {Game1.activeClickableMenu.GetType().FullName} menu crashed while drawing itself. SMAPI will force it to exit to avoid crashing the game.\n{ex.GetLogSummary()}", LogLevel.Error);
                            Game1.activeClickableMenu.exitThisMenu();
                        }
                        events.Rendered.RaiseEmpty();
                        Game1.spriteBatch.End();
                        this.drawOverlays(Game1.spriteBatch);
                        if (target_screen != null)
                        {
                            this.GraphicsDevice.SetRenderTarget((RenderTarget2D)null);
                            this.GraphicsDevice.Clear(Game1.bgColor);
                            Game1.spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.LinearClamp, DepthStencilState.Default, RasterizerState.CullNone);
                            Game1.spriteBatch.Draw((Texture2D)target_screen, Vector2.Zero, new Microsoft.Xna.Framework.Rectangle?(target_screen.Bounds), Microsoft.Xna.Framework.Color.White, 0.0f, Vector2.Zero, Game1.options.zoomLevel, SpriteEffects.None, 1f);
                            Game1.spriteBatch.End();
                        }
                        if (Game1.overlayMenu == null)
                            return;
                        Game1.spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp, (DepthStencilState)null, (RasterizerState)null);
                        Game1.overlayMenu.draw(Game1.spriteBatch);
                        Game1.spriteBatch.End();
                    }
                    else if (Game1.gameMode == (byte)11)
                    {
                        Game1.spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp, (DepthStencilState)null, (RasterizerState)null);
                        events.Rendering.RaiseEmpty();
                        Game1.spriteBatch.DrawString(Game1.dialogueFont, Game1.content.LoadString("Strings\\StringsFromCSFiles:Game1.cs.3685"), new Vector2(16f, 16f), Microsoft.Xna.Framework.Color.HotPink);
                        Game1.spriteBatch.DrawString(Game1.dialogueFont, Game1.content.LoadString("Strings\\StringsFromCSFiles:Game1.cs.3686"), new Vector2(16f, 32f), new Microsoft.Xna.Framework.Color(0, (int)byte.MaxValue, 0));
                        Game1.spriteBatch.DrawString(Game1.dialogueFont, Game1.parseText(Game1.errorMessage, Game1.dialogueFont, Game1.graphics.GraphicsDevice.Viewport.Width), new Vector2(16f, 48f), Microsoft.Xna.Framework.Color.White);
                        events.Rendered.RaiseEmpty();
                        Game1.spriteBatch.End();
                    }
                    else if (Game1.currentMinigame != null)
                    {
                        int batchEnds = 0;

                        if (events.Rendering.HasListeners())
                        {
                            Game1.spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp, null, null);
                            events.Rendering.RaiseEmpty();
                            Game1.spriteBatch.End();
                        }
                        Game1.currentMinigame.draw(Game1.spriteBatch);
                        if (Game1.globalFade && !Game1.menuUp && (!Game1.nameSelectUp || Game1.messagePause))
                        {
                            Game1.spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp, (DepthStencilState)null, (RasterizerState)null);
                            Game1.spriteBatch.Draw(Game1.fadeToBlackRect, Game1.graphics.GraphicsDevice.Viewport.Bounds, Microsoft.Xna.Framework.Color.Black * (Game1.gameMode == (byte)0 ? 1f - Game1.fadeToBlackAlpha : Game1.fadeToBlackAlpha));
                            Game1.spriteBatch.End();
                        }
                        this.drawOverlays(Game1.spriteBatch);
                        if (target_screen == null)
                        {
                            if (++batchEnds == 1 && events.Rendered.HasListeners())
                            {
                                Game1.spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp, null, null);
                                events.Rendered.RaiseEmpty();
                                Game1.spriteBatch.End();
                            }
                            return;
                        }
                        this.GraphicsDevice.SetRenderTarget((RenderTarget2D)null);
                        this.GraphicsDevice.Clear(Game1.bgColor);
                        Game1.spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.LinearClamp, DepthStencilState.Default, RasterizerState.CullNone);
                        Game1.spriteBatch.Draw((Texture2D)target_screen, Vector2.Zero, new Microsoft.Xna.Framework.Rectangle?(target_screen.Bounds), Microsoft.Xna.Framework.Color.White, 0.0f, Vector2.Zero, Game1.options.zoomLevel, SpriteEffects.None, 1f);
                        if (++batchEnds == 1)
                            events.Rendered.RaiseEmpty();
                        Game1.spriteBatch.End();
                    }
                    else if (Game1.showingEndOfNightStuff)
                    {
                        Game1.spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp, (DepthStencilState)null, (RasterizerState)null);
                        events.Rendering.RaiseEmpty();
                        if (Game1.activeClickableMenu != null)
                        {
                            try
                            {
                                events.RenderingActiveMenu.RaiseEmpty();
                                Game1.activeClickableMenu.draw(Game1.spriteBatch);
                                events.RenderedActiveMenu.RaiseEmpty();
                            }
                            catch (Exception ex)
                            {
                                this.Monitor.Log($"The {Game1.activeClickableMenu.GetType().FullName} menu crashed while drawing itself during end-of-night-stuff. SMAPI will force it to exit to avoid crashing the game.\n{ex.GetLogSummary()}", LogLevel.Error);
                                Game1.activeClickableMenu.exitThisMenu();
                            }
                        }
                        events.Rendered.RaiseEmpty();
                        Game1.spriteBatch.End();
                        this.drawOverlays(Game1.spriteBatch);
                        if (target_screen == null)
                            return;
                        this.GraphicsDevice.SetRenderTarget((RenderTarget2D)null);
                        this.GraphicsDevice.Clear(Game1.bgColor);
                        Game1.spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.LinearClamp, DepthStencilState.Default, RasterizerState.CullNone);
                        Game1.spriteBatch.Draw((Texture2D)target_screen, Vector2.Zero, new Microsoft.Xna.Framework.Rectangle?(target_screen.Bounds), Microsoft.Xna.Framework.Color.White, 0.0f, Vector2.Zero, Game1.options.zoomLevel, SpriteEffects.None, 1f);
                        Game1.spriteBatch.End();
                    }
                    else if (Game1.gameMode == (byte)6 || Game1.gameMode == (byte)3 && Game1.currentLocation == null)
                    {
                        Game1.spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp, (DepthStencilState)null, (RasterizerState)null);
                        events.Rendering.RaiseEmpty();
                        string str1 = "";
                        for (int index = 0; (double)index < gameTime.TotalGameTime.TotalMilliseconds % 999.0 / 333.0; ++index)
                            str1 += ".";
                        string str2 = Game1.content.LoadString("Strings\\StringsFromCSFiles:Game1.cs.3688");
                        string s = str2 + str1;
                        string str3 = str2 + "... ";
                        int widthOfString = SpriteText.getWidthOfString(str3, 999999);
                        int height = 64;
                        int x = 64;
                        int y = Game1.graphics.GraphicsDevice.Viewport.GetTitleSafeArea().Bottom - height;
                        SpriteText.drawString(Game1.spriteBatch, s, x, y, 999999, widthOfString, height, 1f, 0.88f, false, 0, str3, -1, SpriteText.ScrollTextAlignment.Left);
                        events.Rendered.RaiseEmpty();
                        Game1.spriteBatch.End();
                        this.drawOverlays(Game1.spriteBatch);
                        if (target_screen != null)
                        {
                            this.GraphicsDevice.SetRenderTarget((RenderTarget2D)null);
                            this.GraphicsDevice.Clear(Game1.bgColor);
                            Game1.spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.LinearClamp, DepthStencilState.Default, RasterizerState.CullNone);
                            Game1.spriteBatch.Draw((Texture2D)target_screen, Vector2.Zero, new Microsoft.Xna.Framework.Rectangle?(target_screen.Bounds), Microsoft.Xna.Framework.Color.White, 0.0f, Vector2.Zero, Game1.options.zoomLevel, SpriteEffects.None, 1f);
                            Game1.spriteBatch.End();
                        }
                        if (Game1.overlayMenu != null)
                        {
                            Game1.spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp, (DepthStencilState)null, (RasterizerState)null);
                            Game1.overlayMenu.draw(Game1.spriteBatch);
                            Game1.spriteBatch.End();
                        }
                        //base.Draw(gameTime);
                    }
                    else
                    {
                        byte batchOpens = 0; // used for rendering event

                        Microsoft.Xna.Framework.Rectangle rectangle;
                        Viewport viewport;
                        if (Game1.gameMode == (byte)0)
                        {
                            Game1.spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp, (DepthStencilState)null, (RasterizerState)null);
                            if (++batchOpens == 1)
                                events.Rendering.RaiseEmpty();
                        }
                        else
                        {
                            if (Game1.drawLighting)
                            {
                                this.GraphicsDevice.SetRenderTarget(Game1.lightmap);
                                this.GraphicsDevice.Clear(Microsoft.Xna.Framework.Color.White * 0.0f);
                                Game1.spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.NonPremultiplied, SamplerState.PointClamp, (DepthStencilState)null, (RasterizerState)null);
                                if (++batchOpens == 1)
                                    events.Rendering.RaiseEmpty();
                                Microsoft.Xna.Framework.Color color = !Game1.currentLocation.Name.StartsWith("UndergroundMine") || !(Game1.currentLocation is MineShaft) ? (Game1.ambientLight.Equals(Microsoft.Xna.Framework.Color.White) || Game1.isRaining && (bool)(NetFieldBase<bool, NetBool>)Game1.currentLocation.isOutdoors ? Game1.outdoorLight : Game1.ambientLight) : (Game1.currentLocation as MineShaft).getLightingColor(gameTime);
                                Game1.spriteBatch.Draw(Game1.staminaRect, Game1.lightmap.Bounds, color);
                                foreach (LightSource currentLightSource in Game1.currentLightSources)
                                {
                                    if (!Game1.isRaining && !Game1.isDarkOut() || currentLightSource.lightContext.Value != LightSource.LightContext.WindowLight)
                                    {
                                        if (currentLightSource.PlayerID != 0L && currentLightSource.PlayerID != Game1.player.UniqueMultiplayerID)
                                        {
                                            Farmer farmerMaybeOffline = Game1.getFarmerMaybeOffline(currentLightSource.PlayerID);
                                            if (farmerMaybeOffline == null || farmerMaybeOffline.currentLocation != null && farmerMaybeOffline.currentLocation.Name != Game1.currentLocation.Name || (bool)(NetFieldBase<bool, NetBool>)farmerMaybeOffline.hidden)
                                                continue;
                                        }
                                        if (Utility.isOnScreen((Vector2)(NetFieldBase<Vector2, NetVector2>)currentLightSource.position, (int)((double)(float)(NetFieldBase<float, NetFloat>)currentLightSource.radius * 64.0 * 4.0)))
                                            Game1.spriteBatch.Draw(currentLightSource.lightTexture, Game1.GlobalToLocal(Game1.viewport, (Vector2)(NetFieldBase<Vector2, NetVector2>)currentLightSource.position) / (float)(Game1.options.lightingQuality / 2), new Microsoft.Xna.Framework.Rectangle?(currentLightSource.lightTexture.Bounds), (Microsoft.Xna.Framework.Color)(NetFieldBase<Microsoft.Xna.Framework.Color, NetColor>)currentLightSource.color, 0.0f, new Vector2((float)currentLightSource.lightTexture.Bounds.Center.X, (float)currentLightSource.lightTexture.Bounds.Center.Y), (float)(NetFieldBase<float, NetFloat>)currentLightSource.radius / (float)(Game1.options.lightingQuality / 2), SpriteEffects.None, 0.9f);
                                    }
                                }
                                Game1.spriteBatch.End();
                                this.GraphicsDevice.SetRenderTarget(target_screen);
                            }
                            if (Game1.bloomDay && Game1.bloom != null)
                                Game1.bloom.BeginDraw();
                            this.GraphicsDevice.Clear(Game1.bgColor);
                            Game1.spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp, (DepthStencilState)null, (RasterizerState)null);
                            if (++batchOpens == 1)
                                events.Rendering.RaiseEmpty();
                            events.RenderingWorld.RaiseEmpty();
                            if (Game1.background != null)
                                Game1.background.draw(Game1.spriteBatch);
                            Game1.mapDisplayDevice.BeginScene(Game1.spriteBatch);
                            Game1.currentLocation.Map.GetLayer("Back").Draw(Game1.mapDisplayDevice, Game1.viewport, Location.Origin, false, 4);
                            Game1.currentLocation.drawWater(Game1.spriteBatch);
                            this._farmerShadows.Clear();
                            if (Game1.currentLocation.currentEvent != null && !Game1.currentLocation.currentEvent.isFestival && Game1.currentLocation.currentEvent.farmerActors.Count > 0)
                            {
                                foreach (Farmer farmerActor in Game1.currentLocation.currentEvent.farmerActors)
                                {
                                    if (farmerActor.IsLocalPlayer && Game1.displayFarmer || !(bool)(NetFieldBase<bool, NetBool>)farmerActor.hidden)
                                        this._farmerShadows.Add(farmerActor);
                                }
                            }
                            else
                            {
                                foreach (Farmer farmer in Game1.currentLocation.farmers)
                                {
                                    if (farmer.IsLocalPlayer && Game1.displayFarmer || !(bool)(NetFieldBase<bool, NetBool>)farmer.hidden)
                                        this._farmerShadows.Add(farmer);
                                }
                            }
                            if (!Game1.currentLocation.shouldHideCharacters())
                            {
                                if (Game1.CurrentEvent == null)
                                {
                                    foreach (NPC character in Game1.currentLocation.characters)
                                    {
                                        if (!(bool)(NetFieldBase<bool, NetBool>)character.swimming && !character.HideShadow && (!character.IsInvisible && !Game1.currentLocation.shouldShadowBeDrawnAboveBuildingsLayer(character.getTileLocation())))
                                            Game1.spriteBatch.Draw(Game1.shadowTexture, Game1.GlobalToLocal(Game1.viewport, character.Position + new Vector2((float)(character.Sprite.SpriteWidth * 4) / 2f, (float)(character.GetBoundingBox().Height + (character.IsMonster ? 0 : 12)))), new Microsoft.Xna.Framework.Rectangle?(Game1.shadowTexture.Bounds), Microsoft.Xna.Framework.Color.White, 0.0f, new Vector2((float)Game1.shadowTexture.Bounds.Center.X, (float)Game1.shadowTexture.Bounds.Center.Y), (float)(4.0 + (double)character.yJumpOffset / 40.0) * (float)(NetFieldBase<float, NetFloat>)character.scale, SpriteEffects.None, Math.Max(0.0f, (float)character.getStandingY() / 10000f) - 1E-06f);
                                    }
                                }
                                else
                                {
                                    foreach (NPC actor in Game1.CurrentEvent.actors)
                                    {
                                        if (!(bool)(NetFieldBase<bool, NetBool>)actor.swimming && !actor.HideShadow && !Game1.currentLocation.shouldShadowBeDrawnAboveBuildingsLayer(actor.getTileLocation()))
                                            Game1.spriteBatch.Draw(Game1.shadowTexture, Game1.GlobalToLocal(Game1.viewport, actor.Position + new Vector2((float)(actor.Sprite.SpriteWidth * 4) / 2f, (float)(actor.GetBoundingBox().Height + (actor.IsMonster ? 0 : (actor.Sprite.SpriteHeight <= 16 ? -4 : 12))))), new Microsoft.Xna.Framework.Rectangle?(Game1.shadowTexture.Bounds), Microsoft.Xna.Framework.Color.White, 0.0f, new Vector2((float)Game1.shadowTexture.Bounds.Center.X, (float)Game1.shadowTexture.Bounds.Center.Y), (float)(4.0 + (double)actor.yJumpOffset / 40.0) * (float)(NetFieldBase<float, NetFloat>)actor.scale, SpriteEffects.None, Math.Max(0.0f, (float)actor.getStandingY() / 10000f) - 1E-06f);
                                    }
                                }
                                foreach (Farmer farmerShadow in this._farmerShadows)
                                {
                                    if (!Game1.multiplayer.isDisconnecting(farmerShadow.UniqueMultiplayerID) && !(bool)(NetFieldBase<bool, NetBool>)farmerShadow.swimming && !farmerShadow.isRidingHorse() && (Game1.currentLocation == null || !Game1.currentLocation.shouldShadowBeDrawnAboveBuildingsLayer(farmerShadow.getTileLocation())))
                                    {
                                        SpriteBatch spriteBatch = Game1.spriteBatch;
                                        Texture2D shadowTexture = Game1.shadowTexture;
                                        Vector2 local = Game1.GlobalToLocal(farmerShadow.Position + new Vector2(32f, 24f));
                                        Microsoft.Xna.Framework.Rectangle? sourceRectangle = new Microsoft.Xna.Framework.Rectangle?(Game1.shadowTexture.Bounds);
                                        Microsoft.Xna.Framework.Color white = Microsoft.Xna.Framework.Color.White;
                                        Microsoft.Xna.Framework.Rectangle bounds = Game1.shadowTexture.Bounds;
                                        double x = (double)bounds.Center.X;
                                        bounds = Game1.shadowTexture.Bounds;
                                        double y = (double)bounds.Center.Y;
                                        Vector2 origin = new Vector2((float)x, (float)y);
                                        double num = 4.0 - (!farmerShadow.running && !farmerShadow.UsingTool || farmerShadow.FarmerSprite.currentAnimationIndex <= 1 ? 0.0 : (double)Math.Abs(FarmerRenderer.featureYOffsetPerFrame[farmerShadow.FarmerSprite.CurrentFrame]) * 0.5);
                                        spriteBatch.Draw(shadowTexture, local, sourceRectangle, white, 0.0f, origin, (float)num, SpriteEffects.None, 0.0f);
                                    }
                                }
                            }
                            Layer layer1 = Game1.currentLocation.Map.GetLayer("Buildings");
                            layer1.Draw(Game1.mapDisplayDevice, Game1.viewport, Location.Origin, false, 4);
                            Game1.mapDisplayDevice.EndScene();
                            Game1.spriteBatch.End();
                            Game1.spriteBatch.Begin(SpriteSortMode.FrontToBack, BlendState.AlphaBlend, SamplerState.PointClamp, (DepthStencilState)null, (RasterizerState)null);
                            if (!Game1.currentLocation.shouldHideCharacters())
                            {
                                if (Game1.CurrentEvent == null)
                                {
                                    foreach (NPC character in Game1.currentLocation.characters)
                                    {
                                        if (!(bool)(NetFieldBase<bool, NetBool>)character.swimming && !character.HideShadow && (!(bool)(NetFieldBase<bool, NetBool>)character.isInvisible && Game1.currentLocation.shouldShadowBeDrawnAboveBuildingsLayer(character.getTileLocation())))
                                            Game1.spriteBatch.Draw(Game1.shadowTexture, Game1.GlobalToLocal(Game1.viewport, character.Position + new Vector2((float)(character.Sprite.SpriteWidth * 4) / 2f, (float)(character.GetBoundingBox().Height + (character.IsMonster ? 0 : 12)))), new Microsoft.Xna.Framework.Rectangle?(Game1.shadowTexture.Bounds), Microsoft.Xna.Framework.Color.White, 0.0f, new Vector2((float)Game1.shadowTexture.Bounds.Center.X, (float)Game1.shadowTexture.Bounds.Center.Y), (float)(4.0 + (double)character.yJumpOffset / 40.0) * (float)(NetFieldBase<float, NetFloat>)character.scale, SpriteEffects.None, Math.Max(0.0f, (float)character.getStandingY() / 10000f) - 1E-06f);
                                    }
                                }
                                else
                                {
                                    foreach (NPC actor in Game1.CurrentEvent.actors)
                                    {
                                        if (!(bool)(NetFieldBase<bool, NetBool>)actor.swimming && !actor.HideShadow && Game1.currentLocation.shouldShadowBeDrawnAboveBuildingsLayer(actor.getTileLocation()))
                                            Game1.spriteBatch.Draw(Game1.shadowTexture, Game1.GlobalToLocal(Game1.viewport, actor.Position + new Vector2((float)(actor.Sprite.SpriteWidth * 4) / 2f, (float)(actor.GetBoundingBox().Height + (actor.IsMonster ? 0 : 12)))), new Microsoft.Xna.Framework.Rectangle?(Game1.shadowTexture.Bounds), Microsoft.Xna.Framework.Color.White, 0.0f, new Vector2((float)Game1.shadowTexture.Bounds.Center.X, (float)Game1.shadowTexture.Bounds.Center.Y), (float)(4.0 + (double)actor.yJumpOffset / 40.0) * (float)(NetFieldBase<float, NetFloat>)actor.scale, SpriteEffects.None, Math.Max(0.0f, (float)actor.getStandingY() / 10000f) - 1E-06f);
                                    }
                                }
                                foreach (Farmer farmerShadow in this._farmerShadows)
                                {
                                    float num1 = Math.Max(0.0001f, farmerShadow.getDrawLayer() + 0.00011f) - 0.0001f;
                                    if (!(bool)(NetFieldBase<bool, NetBool>)farmerShadow.swimming && !farmerShadow.isRidingHorse() && (Game1.currentLocation != null && Game1.currentLocation.shouldShadowBeDrawnAboveBuildingsLayer(farmerShadow.getTileLocation())))
                                    {
                                        SpriteBatch spriteBatch = Game1.spriteBatch;
                                        Texture2D shadowTexture = Game1.shadowTexture;
                                        Vector2 local = Game1.GlobalToLocal(farmerShadow.Position + new Vector2(32f, 24f));
                                        Microsoft.Xna.Framework.Rectangle? sourceRectangle = new Microsoft.Xna.Framework.Rectangle?(Game1.shadowTexture.Bounds);
                                        Microsoft.Xna.Framework.Color white = Microsoft.Xna.Framework.Color.White;
                                        Microsoft.Xna.Framework.Rectangle bounds = Game1.shadowTexture.Bounds;
                                        double x = (double)bounds.Center.X;
                                        bounds = Game1.shadowTexture.Bounds;
                                        double y = (double)bounds.Center.Y;
                                        Vector2 origin = new Vector2((float)x, (float)y);
                                        double num2 = 4.0 - (!farmerShadow.running && !farmerShadow.UsingTool || farmerShadow.FarmerSprite.currentAnimationIndex <= 1 ? 0.0 : (double)Math.Abs(FarmerRenderer.featureYOffsetPerFrame[farmerShadow.FarmerSprite.CurrentFrame]) * 0.5);
                                        double num3 = (double)num1;
                                        spriteBatch.Draw(shadowTexture, local, sourceRectangle, white, 0.0f, origin, (float)num2, SpriteEffects.None, (float)num3);
                                    }
                                }
                            }
                            if ((Game1.eventUp || Game1.killScreen) && (!Game1.killScreen && Game1.currentLocation.currentEvent != null))
                                Game1.currentLocation.currentEvent.draw(Game1.spriteBatch);
                            if (Game1.player.currentUpgrade != null && Game1.player.currentUpgrade.daysLeftTillUpgradeDone <= 3 && Game1.currentLocation.Name.Equals("Farm"))
                                Game1.spriteBatch.Draw(Game1.player.currentUpgrade.workerTexture, Game1.GlobalToLocal(Game1.viewport, Game1.player.currentUpgrade.positionOfCarpenter), new Microsoft.Xna.Framework.Rectangle?(Game1.player.currentUpgrade.getSourceRectangle()), Microsoft.Xna.Framework.Color.White, 0.0f, Vector2.Zero, 1f, SpriteEffects.None, (float)(((double)Game1.player.currentUpgrade.positionOfCarpenter.Y + 48.0) / 10000.0));
                            Game1.currentLocation.draw(Game1.spriteBatch);
                            foreach (Vector2 key in Game1.crabPotOverlayTiles.Keys)
                            {
                                Tile tile = layer1.Tiles[(int)key.X, (int)key.Y];
                                if (tile != null)
                                {
                                    Vector2 local = Game1.GlobalToLocal(Game1.viewport, key * 64f);
                                    Location location = new Location((int)local.X, (int)local.Y);
                                    Game1.mapDisplayDevice.DrawTile(tile, location, (float)(((double)key.Y * 64.0 - 1.0) / 10000.0));
                                }
                            }
                            if (Game1.eventUp && Game1.currentLocation.currentEvent != null)
                            {
                                string messageToScreen = Game1.currentLocation.currentEvent.messageToScreen;
                            }
                            if (Game1.player.ActiveObject == null && (Game1.player.UsingTool || Game1.pickingTool) && (Game1.player.CurrentTool != null && (!Game1.player.CurrentTool.Name.Equals("Seeds") || Game1.pickingTool)))
                                Game1.drawTool(Game1.player);
                            if (Game1.currentLocation.Name.Equals("Farm"))
                                this.drawFarmBuildings();
                            if (Game1.tvStation >= 0)
                                Game1.spriteBatch.Draw(Game1.tvStationTexture, Game1.GlobalToLocal(Game1.viewport, new Vector2(400f, 160f)), new Microsoft.Xna.Framework.Rectangle?(new Microsoft.Xna.Framework.Rectangle(Game1.tvStation * 24, 0, 24, 15)), Microsoft.Xna.Framework.Color.White, 0.0f, Vector2.Zero, 4f, SpriteEffects.None, 1E-08f);
                            if (Game1.panMode)
                            {
                                Game1.spriteBatch.Draw(Game1.fadeToBlackRect, new Microsoft.Xna.Framework.Rectangle((int)Math.Floor((double)(Game1.getOldMouseX() + Game1.viewport.X) / 64.0) * 64 - Game1.viewport.X, (int)Math.Floor((double)(Game1.getOldMouseY() + Game1.viewport.Y) / 64.0) * 64 - Game1.viewport.Y, 64, 64), Microsoft.Xna.Framework.Color.Lime * 0.75f);
                                foreach (Warp warp in (NetList<Warp, NetRef<Warp>>)Game1.currentLocation.warps)
                                    Game1.spriteBatch.Draw(Game1.fadeToBlackRect, new Microsoft.Xna.Framework.Rectangle(warp.X * 64 - Game1.viewport.X, warp.Y * 64 - Game1.viewport.Y, 64, 64), Microsoft.Xna.Framework.Color.Red * 0.75f);
                            }
                            Game1.mapDisplayDevice.BeginScene(Game1.spriteBatch);
                            Game1.currentLocation.Map.GetLayer("Front").Draw(Game1.mapDisplayDevice, Game1.viewport, Location.Origin, false, 4);
                            Game1.mapDisplayDevice.EndScene();
                            Game1.currentLocation.drawAboveFrontLayer(Game1.spriteBatch);
                            Game1.spriteBatch.End();
                            Game1.spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp, (DepthStencilState)null, (RasterizerState)null);
                            if (Game1.displayFarmer && Game1.player.ActiveObject != null && ((bool)(NetFieldBase<bool, NetBool>)Game1.player.ActiveObject.bigCraftable && this.checkBigCraftableBoundariesForFrontLayer()) && Game1.currentLocation.Map.GetLayer("Front").PickTile(new Location(Game1.player.getStandingX(), Game1.player.getStandingY()), Game1.viewport.Size) == null)
                                Game1.drawPlayerHeldObject(Game1.player);
                            else if (Game1.displayFarmer && Game1.player.ActiveObject != null)
                            {
                                if (Game1.currentLocation.Map.GetLayer("Front").PickTile(new Location((int)Game1.player.Position.X, (int)Game1.player.Position.Y - 38), Game1.viewport.Size) == null || Game1.currentLocation.Map.GetLayer("Front").PickTile(new Location((int)Game1.player.Position.X, (int)Game1.player.Position.Y - 38), Game1.viewport.Size).TileIndexProperties.ContainsKey("FrontAlways"))
                                {
                                    Layer layer2 = Game1.currentLocation.Map.GetLayer("Front");
                                    rectangle = Game1.player.GetBoundingBox();
                                    Location mapDisplayLocation1 = new Location(rectangle.Right, (int)Game1.player.Position.Y - 38);
                                    xTile.Dimensions.Size size1 = Game1.viewport.Size;
                                    if (layer2.PickTile(mapDisplayLocation1, size1) != null)
                                    {
                                        Layer layer3 = Game1.currentLocation.Map.GetLayer("Front");
                                        rectangle = Game1.player.GetBoundingBox();
                                        Location mapDisplayLocation2 = new Location(rectangle.Right, (int)Game1.player.Position.Y - 38);
                                        xTile.Dimensions.Size size2 = Game1.viewport.Size;
                                        if (layer3.PickTile(mapDisplayLocation2, size2).TileIndexProperties.ContainsKey("FrontAlways"))
                                            goto label_139;
                                    }
                                    else
                                        goto label_139;
                                }
                                Game1.drawPlayerHeldObject(Game1.player);
                            }
                        label_139:
                            if ((Game1.player.UsingTool || Game1.pickingTool) && Game1.player.CurrentTool != null && ((!Game1.player.CurrentTool.Name.Equals("Seeds") || Game1.pickingTool) && (Game1.currentLocation.Map.GetLayer("Front").PickTile(new Location(Game1.player.getStandingX(), (int)Game1.player.Position.Y - 38), Game1.viewport.Size) != null && Game1.currentLocation.Map.GetLayer("Front").PickTile(new Location(Game1.player.getStandingX(), Game1.player.getStandingY()), Game1.viewport.Size) == null)))
                                Game1.drawTool(Game1.player);
                            if (Game1.currentLocation.Map.GetLayer("AlwaysFront") != null)
                            {
                                Game1.mapDisplayDevice.BeginScene(Game1.spriteBatch);
                                Game1.currentLocation.Map.GetLayer("AlwaysFront").Draw(Game1.mapDisplayDevice, Game1.viewport, Location.Origin, false, 4);
                                Game1.mapDisplayDevice.EndScene();
                            }
                            if ((double)Game1.toolHold > 400.0 && Game1.player.CurrentTool.UpgradeLevel >= 1 && Game1.player.canReleaseTool)
                            {
                                Microsoft.Xna.Framework.Color color = Microsoft.Xna.Framework.Color.White;
                                switch ((int)((double)Game1.toolHold / 600.0) + 2)
                                {
                                    case 1:
                                        color = Tool.copperColor;
                                        break;
                                    case 2:
                                        color = Tool.steelColor;
                                        break;
                                    case 3:
                                        color = Tool.goldColor;
                                        break;
                                    case 4:
                                        color = Tool.iridiumColor;
                                        break;
                                }
                                Game1.spriteBatch.Draw(Game1.littleEffect, new Microsoft.Xna.Framework.Rectangle((int)Game1.player.getLocalPosition(Game1.viewport).X - 2, (int)Game1.player.getLocalPosition(Game1.viewport).Y - (Game1.player.CurrentTool.Name.Equals("Watering Can") ? 0 : 64) - 2, (int)((double)Game1.toolHold % 600.0 * 0.0799999982118607) + 4, 12), Microsoft.Xna.Framework.Color.Black);
                                Game1.spriteBatch.Draw(Game1.littleEffect, new Microsoft.Xna.Framework.Rectangle((int)Game1.player.getLocalPosition(Game1.viewport).X, (int)Game1.player.getLocalPosition(Game1.viewport).Y - (Game1.player.CurrentTool.Name.Equals("Watering Can") ? 0 : 64), (int)((double)Game1.toolHold % 600.0 * 0.0799999982118607), 8), color);
                            }
                            this.drawWeather(gameTime, target_screen);
                            if (Game1.farmEvent != null)
                                Game1.farmEvent.draw(Game1.spriteBatch);
                            if ((double)Game1.currentLocation.LightLevel > 0.0 && Game1.timeOfDay < 2000)
                                Game1.spriteBatch.Draw(Game1.fadeToBlackRect, Game1.graphics.GraphicsDevice.Viewport.Bounds, Microsoft.Xna.Framework.Color.Black * Game1.currentLocation.LightLevel);
                            if (Game1.screenGlow)
                                Game1.spriteBatch.Draw(Game1.fadeToBlackRect, Game1.graphics.GraphicsDevice.Viewport.Bounds, Game1.screenGlowColor * Game1.screenGlowAlpha);
                            Game1.currentLocation.drawAboveAlwaysFrontLayer(Game1.spriteBatch);
                            if (Game1.player.CurrentTool != null && Game1.player.CurrentTool is FishingRod && ((Game1.player.CurrentTool as FishingRod).isTimingCast || (double)(Game1.player.CurrentTool as FishingRod).castingChosenCountdown > 0.0 || ((Game1.player.CurrentTool as FishingRod).fishCaught || (Game1.player.CurrentTool as FishingRod).showingTreasure)))
                                Game1.player.CurrentTool.draw(Game1.spriteBatch);
                            Game1.spriteBatch.End();
                            Game1.spriteBatch.Begin(SpriteSortMode.FrontToBack, BlendState.AlphaBlend, SamplerState.PointClamp, (DepthStencilState)null, (RasterizerState)null);
                            if (Game1.eventUp && Game1.currentLocation.currentEvent != null)
                            {
                                foreach (NPC actor in Game1.currentLocation.currentEvent.actors)
                                {
                                    if (actor.isEmoting)
                                    {
                                        Vector2 localPosition = actor.getLocalPosition(Game1.viewport);
                                        localPosition.Y -= 140f;
                                        if (actor.Age == 2)
                                            localPosition.Y += 32f;
                                        else if (actor.Gender == 1)
                                            localPosition.Y += 10f;
                                        Game1.spriteBatch.Draw(Game1.emoteSpriteSheet, localPosition, new Microsoft.Xna.Framework.Rectangle?(new Microsoft.Xna.Framework.Rectangle(actor.CurrentEmoteIndex * 16 % Game1.emoteSpriteSheet.Width, actor.CurrentEmoteIndex * 16 / Game1.emoteSpriteSheet.Width * 16, 16, 16)), Microsoft.Xna.Framework.Color.White, 0.0f, Vector2.Zero, 4f, SpriteEffects.None, (float)actor.getStandingY() / 10000f);
                                    }
                                }
                            }
                            Game1.spriteBatch.End();
                            if (Game1.drawLighting)
                            {
                                Game1.spriteBatch.Begin(SpriteSortMode.Deferred, this.lightingBlend, SamplerState.LinearClamp, (DepthStencilState)null, (RasterizerState)null);
                                Game1.spriteBatch.Draw((Texture2D)Game1.lightmap, Vector2.Zero, new Microsoft.Xna.Framework.Rectangle?(Game1.lightmap.Bounds), Microsoft.Xna.Framework.Color.White, 0.0f, Vector2.Zero, (float)(Game1.options.lightingQuality / 2), SpriteEffects.None, 1f);
                                if (Game1.isRaining && (bool)(NetFieldBase<bool, NetBool>)Game1.currentLocation.isOutdoors && !(Game1.currentLocation is Desert))
                                    Game1.spriteBatch.Draw(Game1.staminaRect, Game1.graphics.GraphicsDevice.Viewport.Bounds, Microsoft.Xna.Framework.Color.OrangeRed * 0.45f);
                                Game1.spriteBatch.End();
                            }
                            Game1.spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp, (DepthStencilState)null, (RasterizerState)null);
                            events.RenderedWorld.RaiseEmpty();
                            if (Game1.drawGrid)
                            {
                                int num1 = -Game1.viewport.X % 64;
                                float num2 = (float)(-Game1.viewport.Y % 64);
                                int num3 = num1;
                                while (true)
                                {
                                    int num4 = num3;
                                    viewport = Game1.graphics.GraphicsDevice.Viewport;
                                    int width = viewport.Width;
                                    if (num4 < width)
                                    {
                                        SpriteBatch spriteBatch = Game1.spriteBatch;
                                        Texture2D staminaRect = Game1.staminaRect;
                                        int x = num3;
                                        int y = (int)num2;
                                        viewport = Game1.graphics.GraphicsDevice.Viewport;
                                        int height = viewport.Height;
                                        Microsoft.Xna.Framework.Rectangle destinationRectangle = new Microsoft.Xna.Framework.Rectangle(x, y, 1, height);
                                        Microsoft.Xna.Framework.Color color = Microsoft.Xna.Framework.Color.Red * 0.5f;
                                        spriteBatch.Draw(staminaRect, destinationRectangle, color);
                                        num3 += 64;
                                    }
                                    else
                                        break;
                                }
                                float num5 = num2;
                                while (true)
                                {
                                    double num4 = (double)num5;
                                    viewport = Game1.graphics.GraphicsDevice.Viewport;
                                    double height = (double)viewport.Height;
                                    if (num4 < height)
                                    {
                                        SpriteBatch spriteBatch = Game1.spriteBatch;
                                        Texture2D staminaRect = Game1.staminaRect;
                                        int x = num1;
                                        int y = (int)num5;
                                        viewport = Game1.graphics.GraphicsDevice.Viewport;
                                        int width = viewport.Width;
                                        Microsoft.Xna.Framework.Rectangle destinationRectangle = new Microsoft.Xna.Framework.Rectangle(x, y, width, 1);
                                        Microsoft.Xna.Framework.Color color = Microsoft.Xna.Framework.Color.Red * 0.5f;
                                        spriteBatch.Draw(staminaRect, destinationRectangle, color);
                                        num5 += 64f;
                                    }
                                    else
                                        break;
                                }
                            }
                            if (Game1.currentBillboard != 0 && !this.takingMapScreenshot)
                                this.drawBillboard();
                            if (!Game1.eventUp && Game1.farmEvent == null && (Game1.currentBillboard == 0 && Game1.gameMode == (byte)3) && (!this.takingMapScreenshot && Game1.isOutdoorMapSmallerThanViewport()))
                            {
                                SpriteBatch spriteBatch1 = Game1.spriteBatch;
                                Texture2D fadeToBlackRect1 = Game1.fadeToBlackRect;
                                int width1 = -Math.Min(Game1.viewport.X, 4096);
                                viewport = Game1.graphics.GraphicsDevice.Viewport;
                                int height1 = viewport.Height;
                                Microsoft.Xna.Framework.Rectangle destinationRectangle1 = new Microsoft.Xna.Framework.Rectangle(0, 0, width1, height1);
                                Microsoft.Xna.Framework.Color black1 = Microsoft.Xna.Framework.Color.Black;
                                spriteBatch1.Draw(fadeToBlackRect1, destinationRectangle1, black1);
                                SpriteBatch spriteBatch2 = Game1.spriteBatch;
                                Texture2D fadeToBlackRect2 = Game1.fadeToBlackRect;
                                int x = -Game1.viewport.X + Game1.currentLocation.map.Layers[0].LayerWidth * 64;
                                viewport = Game1.graphics.GraphicsDevice.Viewport;
                                int width2 = Math.Min(4096, viewport.Width - (-Game1.viewport.X + Game1.currentLocation.map.Layers[0].LayerWidth * 64));
                                viewport = Game1.graphics.GraphicsDevice.Viewport;
                                int height2 = viewport.Height;
                                Microsoft.Xna.Framework.Rectangle destinationRectangle2 = new Microsoft.Xna.Framework.Rectangle(x, 0, width2, height2);
                                Microsoft.Xna.Framework.Color black2 = Microsoft.Xna.Framework.Color.Black;
                                spriteBatch2.Draw(fadeToBlackRect2, destinationRectangle2, black2);
                            }
                            if ((Game1.displayHUD || Game1.eventUp) && (Game1.currentBillboard == 0 && Game1.gameMode == (byte)3) && (!Game1.freezeControls && !Game1.panMode && (!Game1.HostPaused && !this.takingMapScreenshot)))
                            {
                                events.RenderingHud.RaiseEmpty();
                                this.drawHUD();
                                events.RenderedHud.RaiseEmpty();
                            }
                            else if (Game1.activeClickableMenu == null)
                            {
                                FarmEvent farmEvent = Game1.farmEvent;
                            }
                            if (Game1.hudMessages.Count > 0 && !this.takingMapScreenshot)
                            {
                                for (int i = Game1.hudMessages.Count - 1; i >= 0; --i)
                                    Game1.hudMessages[i].draw(Game1.spriteBatch, i);
                            }
                        }
                        if (Game1.farmEvent != null)
                            Game1.farmEvent.draw(Game1.spriteBatch);
                        if (Game1.dialogueUp && !Game1.nameSelectUp && !Game1.messagePause && ((Game1.activeClickableMenu == null || !(Game1.activeClickableMenu is DialogueBox)) && !this.takingMapScreenshot))
                            this.drawDialogueBox();
                        if (Game1.progressBar && !this.takingMapScreenshot)
                        {
                            SpriteBatch spriteBatch1 = Game1.spriteBatch;
                            Texture2D fadeToBlackRect = Game1.fadeToBlackRect;
                            int x1 = (Game1.graphics.GraphicsDevice.Viewport.GetTitleSafeArea().Width - Game1.dialogueWidth) / 2;
                            rectangle = Game1.graphics.GraphicsDevice.Viewport.GetTitleSafeArea();
                            int y1 = rectangle.Bottom - 128;
                            int dialogueWidth = Game1.dialogueWidth;
                            Microsoft.Xna.Framework.Rectangle destinationRectangle1 = new Microsoft.Xna.Framework.Rectangle(x1, y1, dialogueWidth, 32);
                            Microsoft.Xna.Framework.Color lightGray = Microsoft.Xna.Framework.Color.LightGray;
                            spriteBatch1.Draw(fadeToBlackRect, destinationRectangle1, lightGray);
                            SpriteBatch spriteBatch2 = Game1.spriteBatch;
                            Texture2D staminaRect = Game1.staminaRect;
                            int x2 = (Game1.graphics.GraphicsDevice.Viewport.GetTitleSafeArea().Width - Game1.dialogueWidth) / 2;
                            rectangle = Game1.graphics.GraphicsDevice.Viewport.GetTitleSafeArea();
                            int y2 = rectangle.Bottom - 128;
                            int width = (int)((double)Game1.pauseAccumulator / (double)Game1.pauseTime * (double)Game1.dialogueWidth);
                            Microsoft.Xna.Framework.Rectangle destinationRectangle2 = new Microsoft.Xna.Framework.Rectangle(x2, y2, width, 32);
                            Microsoft.Xna.Framework.Color dimGray = Microsoft.Xna.Framework.Color.DimGray;
                            spriteBatch2.Draw(staminaRect, destinationRectangle2, dimGray);
                        }
                        if (Game1.eventUp && Game1.currentLocation != null && Game1.currentLocation.currentEvent != null)
                            Game1.currentLocation.currentEvent.drawAfterMap(Game1.spriteBatch);
                        if (Game1.isRaining && Game1.currentLocation != null && ((bool)(NetFieldBase<bool, NetBool>)Game1.currentLocation.isOutdoors && !(Game1.currentLocation is Desert)))
                        {
                            SpriteBatch spriteBatch = Game1.spriteBatch;
                            Texture2D staminaRect = Game1.staminaRect;
                            viewport = Game1.graphics.GraphicsDevice.Viewport;
                            Microsoft.Xna.Framework.Rectangle bounds = viewport.Bounds;
                            Microsoft.Xna.Framework.Color color = Microsoft.Xna.Framework.Color.Blue * 0.2f;
                            spriteBatch.Draw(staminaRect, bounds, color);
                        }
                        if ((Game1.fadeToBlack || Game1.globalFade) && !Game1.menuUp && ((!Game1.nameSelectUp || Game1.messagePause) && !this.takingMapScreenshot))
                        {
                            SpriteBatch spriteBatch = Game1.spriteBatch;
                            Texture2D fadeToBlackRect = Game1.fadeToBlackRect;
                            viewport = Game1.graphics.GraphicsDevice.Viewport;
                            Microsoft.Xna.Framework.Rectangle bounds = viewport.Bounds;
                            Microsoft.Xna.Framework.Color color = Microsoft.Xna.Framework.Color.Black * (Game1.gameMode == (byte)0 ? 1f - Game1.fadeToBlackAlpha : Game1.fadeToBlackAlpha);
                            spriteBatch.Draw(fadeToBlackRect, bounds, color);
                        }
                        else if ((double)Game1.flashAlpha > 0.0 && !this.takingMapScreenshot)
                        {
                            if (Game1.options.screenFlash)
                            {
                                SpriteBatch spriteBatch = Game1.spriteBatch;
                                Texture2D fadeToBlackRect = Game1.fadeToBlackRect;
                                viewport = Game1.graphics.GraphicsDevice.Viewport;
                                Microsoft.Xna.Framework.Rectangle bounds = viewport.Bounds;
                                Microsoft.Xna.Framework.Color color = Microsoft.Xna.Framework.Color.White * Math.Min(1f, Game1.flashAlpha);
                                spriteBatch.Draw(fadeToBlackRect, bounds, color);
                            }
                            Game1.flashAlpha -= 0.1f;
                        }
                        if ((Game1.messagePause || Game1.globalFade) && (Game1.dialogueUp && !this.takingMapScreenshot))
                            this.drawDialogueBox();
                        if (!this.takingMapScreenshot)
                        {
                            foreach (TemporaryAnimatedSprite overlayTempSprite in Game1.screenOverlayTempSprites)
                                overlayTempSprite.draw(Game1.spriteBatch, true, 0, 0, 1f);
                        }
                        if (Game1.debugMode)
                        {
                            StringBuilder debugStringBuilder = Game1._debugStringBuilder;
                            debugStringBuilder.Clear();
                            if (Game1.panMode)
                            {
                                debugStringBuilder.Append((Game1.getOldMouseX() + Game1.viewport.X) / 64);
                                debugStringBuilder.Append(",");
                                debugStringBuilder.Append((Game1.getOldMouseY() + Game1.viewport.Y) / 64);
                            }
                            else
                            {
                                debugStringBuilder.Append("player: ");
                                debugStringBuilder.Append(Game1.player.getStandingX() / 64);
                                debugStringBuilder.Append(", ");
                                debugStringBuilder.Append(Game1.player.getStandingY() / 64);
                            }
                            debugStringBuilder.Append(" mouseTransparency: ");
                            debugStringBuilder.Append(Game1.mouseCursorTransparency);
                            debugStringBuilder.Append(" mousePosition: ");
                            debugStringBuilder.Append(Game1.getMouseX());
                            debugStringBuilder.Append(",");
                            debugStringBuilder.Append(Game1.getMouseY());
                            debugStringBuilder.Append(Environment.NewLine);
                            debugStringBuilder.Append(" mouseWorldPosition: ");
                            debugStringBuilder.Append(Game1.getMouseX() + Game1.viewport.X);
                            debugStringBuilder.Append(",");
                            debugStringBuilder.Append(Game1.getMouseY() + Game1.viewport.Y);
                            debugStringBuilder.Append("  debugOutput: ");
                            debugStringBuilder.Append(Game1.debugOutput);
                            Game1.spriteBatch.DrawString(Game1.smallFont, debugStringBuilder, new Vector2((float)this.GraphicsDevice.Viewport.GetTitleSafeArea().X, (float)(this.GraphicsDevice.Viewport.GetTitleSafeArea().Y + Game1.smallFont.LineSpacing * 8)), Microsoft.Xna.Framework.Color.Red, 0.0f, Vector2.Zero, 1f, SpriteEffects.None, 0.9999999f);
                        }
                        if (Game1.showKeyHelp && !this.takingMapScreenshot)
                            Game1.spriteBatch.DrawString(Game1.smallFont, Game1.keyHelpString, new Vector2(64f, (float)(Game1.viewport.Height - 64 - (Game1.dialogueUp ? 192 + (Game1.isQuestion ? Game1.questionChoices.Count * 64 : 0) : 0)) - Game1.smallFont.MeasureString(Game1.keyHelpString).Y), Microsoft.Xna.Framework.Color.LightGray, 0.0f, Vector2.Zero, 1f, SpriteEffects.None, 0.9999999f);
                        if (Game1.activeClickableMenu != null && !this.takingMapScreenshot)
                        {
                            try
                            {
                                events.RenderingActiveMenu.RaiseEmpty();
                                Game1.activeClickableMenu.draw(Game1.spriteBatch);
                                events.RenderedActiveMenu.RaiseEmpty();
                            }
                            catch (Exception ex)
                            {
                                this.Monitor.Log($"The {Game1.activeClickableMenu.GetType().FullName} menu crashed while drawing itself. SMAPI will force it to exit to avoid crashing the game.\n{ex.GetLogSummary()}", LogLevel.Error);
                                Game1.activeClickableMenu.exitThisMenu();
                            }
                        }
                        else if (Game1.farmEvent != null)
                            Game1.farmEvent.drawAboveEverything(Game1.spriteBatch);
                        if (Game1.emoteMenu != null && !this.takingMapScreenshot)
                            Game1.emoteMenu.draw(Game1.spriteBatch);
                        if (Game1.HostPaused && !this.takingMapScreenshot)
                        {
                            string s = Game1.content.LoadString("Strings\\StringsFromCSFiles:DayTimeMoneyBox.cs.10378");
                            SpriteText.drawStringWithScrollBackground(Game1.spriteBatch, s, 96, 32, "", 1f, -1, SpriteText.ScrollTextAlignment.Left);
                        }

                        events.Rendered.RaiseEmpty();
                        Game1.spriteBatch.End();
                        this.drawOverlays(Game1.spriteBatch);
                        this.renderScreenBuffer(target_screen);
                    }
                }
            }
        }

        /// <summary>Immediately exit the game without saving. This should only be invoked when an irrecoverable fatal error happens that risks save corruption or game-breaking bugs.</summary>
        /// <param name="message">The fatal log message.</param>
        private void ExitGameImmediately(string message)
        {
            this.Monitor.LogFatal(message);
            this.CancellationToken.Cancel();
        }
    }
}
