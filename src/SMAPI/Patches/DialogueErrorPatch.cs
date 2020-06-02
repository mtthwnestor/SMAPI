using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using HarmonyLib;
using StardewModdingAPI.Framework;
using StardewModdingAPI.Framework.Patching;
using StardewModdingAPI.Framework.Reflection;
using StardewValley;

namespace StardewModdingAPI.Patches
{
    /// <summary>A Harmony patch for the <see cref="Dialogue"/> constructor which intercepts invalid dialogue lines and logs an error instead of crashing.</summary>
    /// <remarks>Patch methods must be static for Harmony to work correctly. See the Harmony documentation before renaming patch arguments.</remarks>
    [SuppressMessage("ReSharper", "InconsistentNaming", Justification = "Argument names are defined by Harmony and methods are named for clarity.")]
    [SuppressMessage("ReSharper", "IdentifierTypo", Justification = "Argument names are defined by Harmony and methods are named for clarity.")]
    internal class DialogueErrorPatch : IHarmonyPatch
    {
        /*********
        ** Fields
        *********/
        /// <summary>Writes messages to the console and log file on behalf of the game.</summary>
        private static IMonitor MonitorForGame;

        /// <summary>Simplifies access to private code.</summary>
        private static Reflector Reflection;


        /*********
        ** Accessors
        *********/
        /// <summary>A unique name for this patch.</summary>
        public string Name => nameof(DialogueErrorPatch);


        /*********
        ** Public methods
        *********/
        /// <summary>Construct an instance.</summary>
        /// <param name="monitorForGame">Writes messages to the console and log file on behalf of the game.</param>
        /// <param name="reflector">Simplifies access to private code.</param>
        public DialogueErrorPatch(IMonitor monitorForGame, Reflector reflector)
        {
            DialogueErrorPatch.MonitorForGame = monitorForGame;
            DialogueErrorPatch.Reflection = reflector;
        }


        /// <summary>Apply the Harmony patch.</summary>
        /// <param name="harmony">The Harmony instance.</param>
        public void Apply(Harmony harmony)
        {
            harmony.Patch(
                original: AccessTools.Constructor(typeof(Dialogue), new[] { typeof(string), typeof(NPC) }),
                finalizer: new HarmonyMethod(this.GetType(), nameof(DialogueErrorPatch.Finalize_Dialogue_Constructor))
            );
            harmony.Patch(
                original: AccessTools.Property(typeof(NPC), nameof(NPC.CurrentDialogue)).GetMethod,
                finalizer: new HarmonyMethod(this.GetType(), nameof(DialogueErrorPatch.Finalize_NPC_CurrentDialogue))
            );
        }


        /*********
        ** Private methods
        *********/
        /// <summary>The method to call after the Dialogue constructor.</summary>
        /// <param name="__instance">The instance being patched.</param>
        /// <param name="masterDialogue">The dialogue being parsed.</param>
        /// <param name="speaker">The NPC for which the dialogue is being parsed.</param>
        /// <param name="__exception">The exception thrown by the wrapped method, if any.</param>
        /// <returns>Returns the exception to throw, if any.</returns>
        private static Exception Finalize_Dialogue_Constructor(Dialogue __instance, string masterDialogue, NPC speaker, Exception __exception)
        {
            if (__exception != null)
            {
                // log message
                string name = !string.IsNullOrWhiteSpace(speaker?.Name) ? speaker.Name : null;
                DialogueErrorPatch.MonitorForGame.Log($"Failed parsing dialogue string{(name != null ? $" for {name}" : "")}:\n{masterDialogue}\n{__exception.GetLogSummary()}", LogLevel.Error);

                // set default dialogue
                IReflectedMethod parseDialogueString = DialogueErrorPatch.Reflection.GetMethod(__instance, "parseDialogueString");
                IReflectedMethod checkForSpecialDialogueAttributes = DialogueErrorPatch.Reflection.GetMethod(__instance, "checkForSpecialDialogueAttributes");
                parseDialogueString.Invoke("...");
                checkForSpecialDialogueAttributes.Invoke();
            }

            return null;
        }

        /// <summary>The method to call after <see cref="NPC.CurrentDialogue"/>.</summary>
        /// <param name="__instance">The instance being patched.</param>
        /// <param name="__result">The return value of the original method.</param>
        /// <param name="__exception">The exception thrown by the wrapped method, if any.</param>
        /// <returns>Returns the exception to throw, if any.</returns>
        private static Exception Finalize_NPC_CurrentDialogue(NPC __instance, ref Stack<Dialogue> __result, Exception __exception)
        {
            if (__exception == null)
                return null;

            DialogueErrorPatch.MonitorForGame.Log($"Failed loading current dialogue for NPC {__instance.Name}:\n{__exception.GetLogSummary()}", LogLevel.Error);
            __result = new Stack<Dialogue>();

            return null;
        }
    }
}
