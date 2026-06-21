using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace IP_Phone.Services
{
    /// <summary>
    /// Provides async-safe console I/O for a SIP-phone CLI.
    /// Wraps <see cref="Console.ReadLine"/> with a custom <see cref="ReadLine"/> that supports
    /// tab-completion, command-history navigation (arrow keys), and a dynamic <c>promptFunc</c>
    /// that is re-evaluated each line.
    /// The <see cref="WriteSafe"/> method (used by <see cref="Info"/>, <see cref="Success"/>,
    /// <see cref="Warning"/>, <see cref="Error"/>, <see cref="Event"/>) prevents prompt corruption
    /// when asynchronous SIP events print messages while the user is mid-typing: it temporarily
    /// clears the input line, writes the event, and then restores the prompt + partial input.
    /// </summary>
    public static class CliHelper
    {
        /// <summary>
        /// Static list of all recognised command names (including shorthand aliases) used for
        /// tab-completion filtering.
        /// </summary>
        private static readonly string[] Commands =
        {
            "call", "answer", "a", "reject", "r", "rej", "hangup", "h",
            "redial", "rd", "dtmf", "dt", "hold", "hl", "unhold", "uh",
            "dnd", "forward", "fw", "transfer", "tr", "atransfer", "at",
            "speed", "history", "hist", "phonebook", "pb", "line", "l",
            "lines", "ls", "exit", "quit", "help"
        };

        /// <summary>Rolling list of previously submitted command lines (most recent appended last).</summary>
        private static readonly List<string> History = new List<string>();

        /// <summary>Current position in <see cref="History"/> when navigating with Up/Down arrows.</summary>
        private static int _historyIndex;

        /// <summary>Partial input the user has typed on the current line (not yet submitted).</summary>
        private static StringBuilder _currentInput;

        /// <summary>
        /// Function that produces the current prompt string. Called each time the prompt needs to be
        /// redrawn (e.g. after an async event is printed via <see cref="WriteSafe"/>).
        /// </summary>
        private static Func<string> _promptFunc;

        /// <summary>Length of the prompt text (used by <see cref="WriteSafe"/> to calculate cursor movement).</summary>
        private static int _promptWidth;

        /// <summary>
        /// <c>true</c> while the <see cref="ReadLine"/> loop is active. Used by <see cref="WriteSafe"/>
        /// to decide whether it can safely erase/redraw the input line.
        /// </summary>
        private static bool _inReadLine;

        /// <summary>
        /// Reads one line of user input from the console with tab-completion and history navigation.
        /// The prompt is produced by calling <paramref name="promptFunc"/> each invocation, allowing
        /// the prompt to reflect real-time state (e.g. current call status or active line).
        /// </summary>
        /// <param name="promptFunc">A delegate that returns the current prompt string.</param>
        /// <returns>The trimmed input line, or an empty string if the user pressed Enter immediately.</returns>
        public static string ReadLine(Func<string> promptFunc)
        {
            _inReadLine = true;
            _promptFunc = promptFunc;
            var prompt = promptFunc();
            _promptWidth = prompt.Length;
            _currentInput = new StringBuilder();
            Console.Write(prompt);
            var sb = _currentInput;
            _historyIndex = History.Count;
            var tabCycle = new List<string>();
            var tabIndex = 0;

            while (true)
            {
                var ki = Console.ReadKey(true);

                if (ki.Key == ConsoleKey.Enter)
                {
                    Console.WriteLine();
                    var line = sb.ToString().Trim();
                    if (line.Length > 0)
                        History.Add(line);
                    _inReadLine = false;
                    _currentInput = null;
                    _promptFunc = null;
                    return line;
                }

                if (ki.Key == ConsoleKey.Tab)
                {
                    var partial = sb.ToString().Trim();
                    if (tabCycle.Count == 0)
                    {
                        tabCycle = Commands.Where(c => c.StartsWith(partial) && c != partial).ToList();
                        tabIndex = 0;
                    }
                    if (tabCycle.Count > 0)
                    {
                        var completion = tabCycle[tabIndex % tabCycle.Count] + " ";
                        ReplaceInput(sb, completion);
                        tabIndex++;
                    }
                    continue;
                }

                tabCycle.Clear();

                if (ki.Key == ConsoleKey.Backspace && sb.Length > 0)
                {
                    sb.Length--;
                    Console.Write("\b \b");
                    continue;
                }

                if (ki.Key == ConsoleKey.UpArrow && History.Count > 0)
                {
                    _historyIndex = Math.Max(0, _historyIndex - 1);
                    ReplaceInput(sb, History[_historyIndex]);
                    continue;
                }

                if (ki.Key == ConsoleKey.DownArrow)
                {
                    if (_historyIndex < History.Count - 1)
                    {
                        _historyIndex++;
                        ReplaceInput(sb, History[_historyIndex]);
                    }
                    else
                    {
                        _historyIndex = History.Count;
                        ReplaceInput(sb, "");
                    }
                    continue;
                }

                if (!char.IsControl(ki.KeyChar))
                {
                    sb.Append(ki.KeyChar);
                    Console.Write(ki.KeyChar);
                }
            }
        }

        /// <summary>
        /// Replaces the current line's input buffer in-place without submitting.
        /// Erases the existing visible text (overwriting with spaces), then writes the new text.
        /// Used by history recall and tab-completion.
        /// </summary>
        /// <param name="sb">The <see cref="StringBuilder"/> holding the current input.</param>
        /// <param name="newText">The replacement string to display and store.</param>
        private static void ReplaceInput(StringBuilder sb, string newText)
        {
            var len = sb.Length;
            sb.Clear();
            sb.Append(newText);
            Console.Write(new string('\b', len) + new string(' ', len) + new string('\b', len));
            Console.Write(newText);
        }

        /// <summary>
        /// Writes a message to the console in a way that is safe to call from any thread, even
        /// while <see cref="ReadLine"/> is waiting for user input. If the user is mid-typing,
        /// the current input line is cleared (cursor moved back, blanked, moved back again),
        /// the message is written, and then the prompt and partial input are restored.
        /// This prevents asynchronous SIP events (e.g. incoming call) from corrupting the visual
        /// input line.
        /// </summary>
        /// <param name="message">The text to write (without trailing newline; the writer delegate is expected to add it).</param>
        /// <param name="writer">A delegate that performs the actual <c>Console.Write</c>/<c>Console.WriteLine</c> call, possibly with colour changes.</param>
        private static void WriteSafe(string message, Action<string> writer)
        {
            if (_inReadLine && _currentInput != null && _promptFunc != null)
            {
                var prompt = _promptFunc();
                var input = _currentInput.ToString();
                var oldLen = _promptWidth + input.Length;
                var newLen = prompt.Length + input.Length;
                var clearLen = Math.Max(oldLen, newLen);
                Console.Write(new string('\b', clearLen) + new string(' ', clearLen) + new string('\b', clearLen));
                writer(message);
                Console.Write(prompt + input);
                _promptWidth = prompt.Length;
            }
            else
            {
                writer(message);
            }
        }

        /// <summary>Writes an informational message (default foreground colour) via <see cref="WriteSafe"/>.</summary>
        public static void Info(string msg) => WriteSafe(msg, m => Console.WriteLine(m));

        /// <summary>Writes a success message in <see cref="ConsoleColor.Green"/> via <see cref="WriteSafe"/>.</summary>
        public static void Success(string msg) => WriteSafe(msg, m =>
        {
            var c = Console.ForegroundColor;
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine(m);
            Console.ForegroundColor = c;
        });

        /// <summary>Writes a warning message in <see cref="ConsoleColor.Yellow"/> via <see cref="WriteSafe"/>.</summary>
        public static void Warning(string msg) => WriteSafe(msg, m =>
        {
            var c = Console.ForegroundColor;
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine(m);
            Console.ForegroundColor = c;
        });

        /// <summary>Writes an error message in <see cref="ConsoleColor.Red"/> via <see cref="WriteSafe"/>.</summary>
        public static void Error(string msg) => WriteSafe(msg, m =>
        {
            var c = Console.ForegroundColor;
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine(m);
            Console.ForegroundColor = c;
        });

        /// <summary>Writes a SIP event message in <see cref="ConsoleColor.Cyan"/> via <see cref="WriteSafe"/>.</summary>
        public static void Event(string msg) => WriteSafe(msg, m =>
        {
            var c = Console.ForegroundColor;
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine(m);
            Console.ForegroundColor = c;
        });

        /// <summary>
        /// Configures the console window for CLI use. Currently sets the buffer height to 3000 lines
        /// so that scroll-back can hold a reasonable conversation history.
        /// </summary>
        public static void SetupConsole()
        {
            try
            {
                Console.BufferHeight = 3000;
            }
            catch { }
        }
    }
}
