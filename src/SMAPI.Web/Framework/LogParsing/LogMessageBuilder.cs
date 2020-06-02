using System;
using System.Text;
using StardewModdingAPI.Web.Framework.LogParsing.Models;

namespace StardewModdingAPI.Web.Framework.LogParsing
{
    /// <summary>Handles constructing log message instances with minimal memory allocation.</summary>
    internal class LogMessageBuilder
    {
        /*********
        ** Fields
        *********/
        /// <summary>The local time when the next log was posted.</summary>
        public string Time { get; set; }

        /// <summary>The log level for the next log message.</summary>
        public LogLevel Level { get; set; }

        /// <summary>The mod name for the next log message.</summary>
        public string Mod { get; set; }

        /// <summary>The text for the next log message.</summary>
        private readonly StringBuilder Text = new StringBuilder();


        /*********
        ** Accessors
        *********/
        /// <summary>Whether the next log message has been started.</summary>
        public bool Started { get; private set; }


        /*********
        ** Public methods
        *********/
        /// <summary>Start accumulating values for a new log message.</summary>
        /// <param name="time">The local time when the log was posted.</param>
        /// <param name="level">The log level.</param>
        /// <param name="mod">The mod name.</param>
        /// <param name="text">The initial log text.</param>
        /// <exception cref="InvalidOperationException">A log message is already started; call <see cref="Clear"/> before starting a new message.</exception>
        public void Start(string time, LogLevel level, string mod, string text)
        {
            if (this.Started)
                throw new InvalidOperationException("Can't start new message, previous log message isn't done yet.");

            this.Started = true;

            this.Time = time;
            this.Level = level;
            this.Mod = mod;
            this.Text.Append(text);
        }

        /// <summary>Add a new line to the next log message being built.</summary>
        /// <param name="text">The line to add.</param>
        /// <exception cref="InvalidOperationException">A log message hasn't been started yet.</exception>
        public void AddLine(string text)
        {
            if (!this.Started)
                throw new InvalidOperationException("Can't add text, no log message started yet.");

            this.Text.Append("\n");
            this.Text.Append(text);
        }

        /// <summary>Get a log message for the accumulated values.</summary>
        public LogMessage Build()
        {
            if (!this.Started)
                return null;

            return new LogMessage
            {
                Time = this.Time,
                Level = this.Level,
                Mod = this.Mod,
                Text = this.Text.ToString()
            };
        }

        /// <summary>Reset to start a new log message.</summary>
        public void Clear()
        {
            this.Started = false;
            this.Text.Clear();
        }
    }
}
