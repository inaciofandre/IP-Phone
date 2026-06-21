using IP_Phone.Models;
using IP_Phone.Services;
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;

/// <summary>
/// Main entry point for the Dialex SIP softphone console application.
/// Manages the interactive command loop, SIP registration, account lifecycle,
/// call control, phonebook, speed dials, and call history.
/// </summary>
internal class Program
{
    /// <summary>
    /// Reads the application version from the assembly metadata (major.minor.build).
    /// Falls back to "1.0" if the version attribute is unavailable.
    /// </summary>
    private static string Version => Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0";

    /// <summary>Loaded application settings (SIP ports, codecs, TLS, etc.) from settings.json.</summary>
    private static Settings _settings;

    /// <summary>Manages persisted SIP accounts stored via DPAPI encryption.</summary>
    private static AccountManager _accountManager;

    /// <summary>Core SIP service; handles registration, calls, lines, DND, forwarding, and media.</summary>
    private static SipService _sipService;

    /// <summary>
    /// Application entry point.  Parses CLI arguments, performs first-time account setup,
    /// initialises the SIP service, hooks Ctrl+C, then runs the interactive command loop.
    /// </summary>
    /// <param name="args">Command-line arguments: --reset-credentials, --verbose / -v.</param>
    private static async Task Main(string[] args)
    {
        // ── CLI argument handling ──────────────────────────────────────────────
        // --reset-credentials: clear all stored accounts and credential files, then exit.
        // --verbose / -v:      enable verbose SIP stack logging.
        var verbose = false;
        foreach (var a in args)
        {
            if (a.ToLower() == "--reset-credentials")
            {
                new AccountManager().Clear();
                new CredentialManager().Clear();
                Console.WriteLine("Accounts cleared. Restart the app to set new accounts.");
                return;
            }
            if (a.ToLower() == "--verbose" || a.ToLower() == "-v")
                verbose = true;
        }

        Console.OutputEncoding = System.Text.Encoding.UTF8;

        // ── Load global settings ───────────────────────────────────────────────
        _settings = JsonSerializer.Deserialize<Settings>(File.ReadAllText("settings.json"));
        if (_settings == null) { Console.WriteLine("Failed to load settings."); return; }

        // ── First-run account wizard ──────────────────────────────────────────
        // If no accounts exist yet, try migrating old-format credentials first.
        // If migration yields nothing, show the interactive account setup wizard.
        _accountManager = new AccountManager();
        if (!_accountManager.HasAccounts)
        {
            if (!AccountManager.TryMigrateOldCredentials(null))
            {
                ShowBanner();
                Console.WriteLine("First-Time Account Setup\n");
                var account = PromptAccount();
                if (account == null) return;
                _accountManager.Add(account);
                Console.WriteLine("Account saved (DPAPI encrypted).\n");
            }
        }

        // Load accounts and ensure at least one is marked as default.
        _accountManager.Load();
        if (_accountManager.Default == null)
        {
            Console.WriteLine("No accounts configured. Use --reset-credentials to restart setup.");
            return;
        }

        // ── Initialise SIP service ────────────────────────────────────────────
        _sipService = new SipService(_settings, _accountManager.Default, verbose);

        // ── Ctrl+C handler ────────────────────────────────────────────────────
        // Gracefully shuts down the SIP transport and threads before exiting.
        Console.CancelKeyPress += (sender, e) =>
        {
            e.Cancel = true;
            _sipService.Shutdown();
            Environment.Exit(0);
        };

        // ── Start SIP registration ────────────────────────────────────────────
        ShowBanner();
        await _sipService.StartAsync();

        CliHelper.SetupConsole();
        Console.WriteLine("Type 'help' for commands.\n");

        // ── Interactive command loop ──────────────────────────────────────────
        while (true)
        {
            // Build a dynamic prompt that reflects current line state.
            // Format: L{index}[{call duration}][{flags}]> e.g. "L1[03:42][DND]> " or "L0[OFF]> "
            var line = _sipService.ActiveLine;
            var flags = "";
            if (_sipService.DndEnabled) flags += "[DND]";
            else if (_sipService.ForwardingTarget != null) flags += $"[FWD->{_sipService.ForwardingTarget}]";
            if (!_sipService.IsOnline) flags += "[OFF]";
            if (line.IsMuted) flags += "[MUTED]";
            if (line.IsRecording) flags += "[REC]";

            // Dynamic prompt lambda – re-evaluated on every render so timer/flags stay current.
            var input = CliHelper.ReadLine(() =>
            {
                var l = _sipService.ActiveLine;
                var f = "";
                if (_sipService.DndEnabled) f += "[DND]";
                else if (_sipService.ForwardingTarget != null) f += $"[FWD->{_sipService.ForwardingTarget}]";
                if (!_sipService.IsOnline) f += "[OFF]";
                if (l.IsMuted) f += "[MUTED]";
                if (l.IsRecording) f += "[REC]";
                var t = l.CallDuration.Length > 0 ? $"[{l.CallDuration}]" : "";
                var p = $"L{_sipService.ActiveLineIndex}{t}> {f} ";
                if (f.Length == 0) p = $"L{_sipService.ActiveLineIndex}{t}> ";
                return p;
            });
            if (input == null) break;

            var parts = input.Split(' ');
            var command = parts[0].ToLower();

            switch (command)
            {
                // ── call <number|name> ────────────────────────────────────────
                // Places an outbound call.  Accepts a raw number, a phonebook contact
                // name, or a speed-dial key.  Automatically prefixes the SIP scheme
                // and server domain.
                case "call":
                    if (parts.Length > 1)
                    {
                        var target = parts[1];
                        if (!target.StartsWith("sip:") && !target.StartsWith("sips:"))
                        {
                            var sdNum = _sipService.ResolveSpeedDial(target);
                            if (sdNum != null) target = sdNum;
                            else if (!char.IsDigit(target[0]))
                            {
                                var pbNum = _sipService.Phonebook.GetNumber(target);
                                if (pbNum != null) target = pbNum;
                            }
                            if (!IsValidPhoneNumber(target))
                            { Console.WriteLine("Invalid phone number."); break; }
                            var acc = _sipService.CurrentAccount;
                            var scheme = _settings.UseTls ? "sips" : "sip";
                            target = scheme + ":" + target + "@" + (acc?.Server ?? "");
                        }
                        await _sipService.MakeCallAsync(target);
                    }
                    else Console.WriteLine("Usage: call <number|name>");
                    break;

                // ── answer / a ────────────────────────────────────────────────
                // Answers the ringing call on the active line.
                case "answer": case "a": await _sipService.AnswerCall(); break;

                // ── reject / r / rej ──────────────────────────────────────────
                // Rejects (declines) the incoming call.
                case "reject": case "r": case "rej": await _sipService.RejectCall(); break;

                // ── hangup / h ────────────────────────────────────────────────
                // Hangs up the currently connected call.
                case "hangup": case "h": _sipService.HangUp(); break;

                // ── redial / rd ───────────────────────────────────────────────
                // Dials the most recently called number.
                case "redial": case "rd": await _sipService.Redial(); break;

                // ── transfer / tr <number|name> ────────────────────────────────
                // Blind-transfers the active call to the specified target.
                case "transfer": case "tr":
                    if (parts.Length > 1)
                    {
                        var target = parts[1];
                        var sdNum = _sipService.ResolveSpeedDial(target);
                        if (sdNum != null) target = sdNum;
                        else if (!char.IsDigit(target[0]))
                        { var pbNum = _sipService.Phonebook.GetNumber(target); if (pbNum != null) target = pbNum; }
                        if (!IsValidPhoneNumber(target))
                        { Console.WriteLine("Invalid number."); break; }
                        await _sipService.BlindTransferTo(target);
                    }
                    else Console.WriteLine("Usage: transfer <number|name>");
                    break;

                // ── atransfer / at <number|name> ──────────────────────────────
                // Performs an attended (warm) transfer – places current call on
                // hold, dials the target, then completes the transfer.
                case "atransfer": case "at":
                    if (parts.Length > 1)
                    {
                        var target = parts[1];
                        var sdNum = _sipService.ResolveSpeedDial(target);
                        if (sdNum != null) target = sdNum;
                        else if (!char.IsDigit(target[0]))
                        { var pbNum = _sipService.Phonebook.GetNumber(target); if (pbNum != null) target = pbNum; }
                        if (!IsValidPhoneNumber(target))
                        { Console.WriteLine("Invalid number."); break; }
                        await _sipService.AttendedTransferTo(target);
                    }
                    else Console.WriteLine("Usage: atransfer <number|name>");
                    break;

                // ── dtmf / dt <digit> ─────────────────────────────────────────
                // Sends a DTMF tone via RFC 2833 (out-of-band).
                // Use "dtmf info <digit>" to send via SIP INFO instead.
                case "dtmf": case "dt":
                    if (parts.Length > 2 && (parts[1].ToLower() == "info" || parts[1].ToLower() == "i") && byte.TryParse(parts[2], out var infoDigit))
                        await _sipService.SendDtmfInfo(infoDigit);
                    else if (parts.Length > 1 && byte.TryParse(parts[1], out var digit))
                        await _sipService.SendDtmf(digit);
                    else Console.WriteLine("Usage: dtmf <0-9> or dtmf info <0-9>");
                    break;

                // ── hold / hl ─────────────────────────────────────────────────
                // Places the active call on hold (sends a re-INVITE with a=inactive / sendonly).
                case "hold": case "hl": _sipService.PutOnHold(); break;

                // ── unhold / uh ───────────────────────────────────────────────
                // Takes the held call off hold (sends a re-INVITE restoring sendrecv).
                case "unhold": case "uh": _sipService.TakeOffHold(); break;

                // ── dnd ───────────────────────────────────────────────────────
                // Toggles Do-Not-Disturb mode.  When enabled, all incoming calls
                // are automatically rejected with 480 Temporarily Unavailable.
                case "dnd": _sipService.ToggleDnd(); break;

                // ── forward / fw <number> ─────────────────────────────────────
                // Sets (or displays / disables) unconditional call forwarding.
                // "forward off" clears the forwarding target.
                case "forward": case "fw":
                    if (parts.Length > 1 && parts[1].ToLower() == "off")
                        _sipService.DisableForwarding();
                    else if (parts.Length > 1)
                    {
                        if (!IsValidPhoneNumber(parts[1]))
                        { Console.WriteLine("Invalid number."); break; }
                        _sipService.SetForwarding(parts[1]);
                    }
                    else if (_sipService.ForwardingTarget != null)
                        Console.WriteLine($"Forwarding to {_sipService.ForwardingTarget}");
                    else Console.WriteLine("Usage: forward <number> or forward off");
                    break;

                // ── phonebook / pb ────────────────────────────────────────────
                // Manages the local contact list.
                //   pb add <name> <num>   – add a contact
                //   pb remove <name>      – remove a contact
                //   pb list (default)     – display all contacts
                //   pb clear              – wipe the phonebook
                case "phonebook": case "pb":
                    if (parts.Length > 2 && parts[1].ToLower() == "add")
                    {
                        var name = parts[2];
                        if (name.Length > 50) { Console.WriteLine("Name too long (max 50)."); break; }
                        if (parts.Length > 3 && !IsValidPhoneNumber(parts[3]))
                        { Console.WriteLine("Invalid number."); break; }
                        _sipService.Phonebook.Add(name, parts.Length > 3 ? parts[3] : "");
                        CliHelper.Success($"Added {name} -> {(parts.Length > 3 ? parts[3] : "")}");
                    }
                    else if (parts.Length > 2 && parts[1].ToLower() == "remove")
                    {
                        if (_sipService.Phonebook.Remove(parts[2])) CliHelper.Info($"Removed {parts[2]}");
                        else CliHelper.Warning($"Not found: {parts[2]}");
                    }
                    else if (parts.Length > 1 && parts[1].ToLower() == "clear")
                    { _sipService.Phonebook.Clear(); CliHelper.Info("Phonebook cleared"); }
                    else
                    {
                        var entries = _sipService.Phonebook.GetAll();
                        if (entries.Count == 0) Console.WriteLine("Phonebook is empty.");
                        else
                        {
                            Console.WriteLine($"{"Name",-20} Number"); Console.WriteLine(new string('-', 35));
                            foreach (var e in entries) Console.WriteLine($"{e.Key,-20} {e.Value}");
                        }
                    }
                    break;

                // ── exit / quit ───────────────────────────────────────────────
                // Gracefully shuts down the SIP service and terminates the loop.
                case "exit": case "quit": _sipService.Shutdown(); return;

                // ── history / hist ────────────────────────────────────────────
                // Displays recent or full call history with date, direction,
                // call status, duration, and remote party.
                // "history clear" wipes the log.
                case "history": case "hist":
                    if (parts.Length > 1 && parts[1].ToLower() == "clear")
                    { _sipService.CallHistory.Clear(); Console.WriteLine("Call history cleared."); }
                    else
                    {
                        var entries = parts.Length > 1 && parts[1].ToLower() == "all"
                            ? _sipService.CallHistory.GetAll() : _sipService.CallHistory.GetRecent();
                        if (entries.Count == 0) Console.WriteLine("No call history.");
                        else
                        {
                            Console.WriteLine($"{"Date",-20} {"Dir",-8} {"Status",-10} {"Dur",-7} Remote"); Console.WriteLine(new string('-', 60));
                            foreach (var e in entries)
                            {
                                var dur = e.DurationSeconds.HasValue ? $"{(int)e.DurationSeconds.Value / 60}:{(int)e.DurationSeconds.Value % 59:D2}" : "-";
                                Console.WriteLine($"{e.StartTime:yyyy-MM-dd HH:mm:ss,-20} {e.Direction,-8} {e.Status,-10} {dur,-7} {e.RemoteParty}");
                            }
                        }
                    }
                    break;

                // ── line / l ──────────────────────────────────────────────────
                // Switches the active line by 0-based index.
                case "line": case "l":
                    if (parts.Length > 1 && int.TryParse(parts[1], out var lineIdx)) _sipService.SwitchLine(lineIdx);
                    else Console.WriteLine("Usage: line <0-based index>");
                    break;

                // ── lines / ls ────────────────────────────────────────────────
                // Lists all configured SIP lines with their status, duration,
                // flags (MUTED, REC), and an arrow indicating the active line.
                case "lines": case "ls":
                    Console.WriteLine($"{"Line",-6} {"Status",-12} {"Duration",-8} {"Flags",-16} Active");
                    Console.WriteLine(new string('-', 55));
                    foreach (var l in _sipService.Lines)
                    {
                        var lflags = "";
                        if (l.IsMuted) lflags += "MUTED ";
                        if (l.IsRecording) lflags += "REC ";
                        Console.WriteLine($"{l.Id,-6} {l.StatusString,-12} {l.CallDuration,-8} {lflags,-16} {(_sipService.ActiveLineIndex == l.Id ? "<--" : "")}");
                    }
                    break;

                // ── speed <key> ───────────────────────────────────────────────
                // Speed-dial management and invocation.
                //   speed <key>            – dial the stored number for <key>
                //   speed set <key> <num>   – store a speed-dial entry
                //   speed remove <key>      – delete a speed-dial entry
                //   speed list              – list all speed-dial mappings
                //   speed clear             – remove all speed-dial entries
                case "speed":
                    if (parts.Length > 2 && parts[1].ToLower() == "set")
                    {
                        if (!IsValidPhoneNumber(parts[3])) { Console.WriteLine("Invalid number."); break; }
                        _sipService.SetSpeedDial(parts[2], parts[3]);
                    }
                    else if (parts.Length > 2 && parts[1].ToLower() == "remove")
                        _sipService.RemoveSpeedDial(parts[2]);
                    else if (parts.Length > 1 && parts[1].ToLower() == "clear")
                        _sipService.ClearSpeedDials();
                    else if (parts.Length > 1 && parts[1].ToLower() == "list")
                    {
                        var sd = _sipService.SpeedDials;
                        if (sd.Count == 0) Console.WriteLine("No speed dials.");
                        else { Console.WriteLine($"{"Key",-6} Number"); Console.WriteLine(new string('-', 25)); foreach (var kv in sd) Console.WriteLine($"{kv.Key,-6} {kv.Value}"); }
                    }
                    else if (parts.Length > 1)
                    {
                        var num = _sipService.ResolveSpeedDial(parts[1]);
                        if (num != null)
                        {
                            var acc = _sipService.CurrentAccount;
                            var scheme = _settings.UseTls ? "sips" : "sip";
                            await _sipService.MakeCallAsync(scheme + ":" + num + "@" + (acc?.Server ?? ""));
                        }
                        else Console.WriteLine($"Speed dial '{parts[1]}' not found.");
                    }
                    else Console.WriteLine("Usage: speed <key> | speed set <key> <num> | speed remove <key> | speed list | speed clear");
                    break;

                // ── account / acc ─────────────────────────────────────────────
                // SIP account lifecycle: list, add, remove, edit, set default.
                // Delegates to HandleAccountCommand for shared logic.
                case "account":
                case "accounts":
                case "acc":
                    HandleAccountCommand(parts);
                    break;
                // Funnel a common typo to the intended handler.
                case "acccount":
                    Console.WriteLine("Did you mean 'account'?");
                    HandleAccountCommand(new[] { "account", "list" });
                    break;

                // ── help ──────────────────────────────────────────────────────
                // Prints a summary of all available commands and their aliases.
                case "help":
                    Console.WriteLine("Commands:");
                    Console.WriteLine("  call <num|name>   Place a call (phonebook name or speed dial key)");
                    Console.WriteLine("  answer (a)        Answer incoming call");
                    Console.WriteLine("  reject (r)        Reject incoming call");
                    Console.WriteLine("  hangup (h)        Hang up active call");
                    Console.WriteLine("  redial (rd)       Redial last number");
                    Console.WriteLine("  dtmf <0-9>        Send DTMF tone (RFC 2833)");
                    Console.WriteLine("  dtmf info <0-9>   Send DTMF tone (SIP INFO)");
                    Console.WriteLine("  hold (hl)         Put call on hold");
                    Console.WriteLine("  unhold (uh)       Take call off hold");
                    Console.WriteLine("  dnd               Toggle Do Not Disturb");
                    Console.WriteLine("  forward <num>     Call forward to number");
                    Console.WriteLine("  forward off       Disable call forwarding");
                    Console.WriteLine("  transfer <num>    Blind transfer active call");
                    Console.WriteLine("  atransfer <num>   Attended transfer");
                    Console.WriteLine("  speed <key>       Speed dial or manage (set/remove/list/clear)");
                    Console.WriteLine("  history (hist)    Show call history");
                    Console.WriteLine("  phonebook (pb)    Manage contacts");
                    Console.WriteLine("  account (acc)     Manage SIP accounts (list/add/remove/edit/default)");
                    Console.WriteLine("  line <N> (l)      Switch to line N");
                    Console.WriteLine("  lines (ls)        List line status");
                    Console.WriteLine("  exit              Shutdown and exit");
                    break;

                default:
                    Console.WriteLine("Type 'help' for commands.");
                    break;
            }
        }
    }

    /// <summary>
    /// Reads a password from the console with masked (*) echo.
    /// Handles Backspace for character removal and Enter to confirm.
    /// </summary>
    /// <returns>The password string as entered by the user.</returns>
    private static string ReadPassword()
    {
        var pass = new System.Text.StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(true);
            if (key.Key == ConsoleKey.Enter)
                break;
            if (key.Key == ConsoleKey.Backspace && pass.Length > 0)
            {
                pass.Length--;
                Console.Write("\b \b");
            }
            else if (key.KeyChar >= 32)
            {
                pass.Append(key.KeyChar);
                Console.Write('*');
            }
        }
        return pass.ToString();
    }

    /// <summary>
    /// Validates that the input string contains only characters allowed in a
    /// phone number: digits (0-9), '+', '#', and '*'.  The length must be
    /// between 1 and 50 characters inclusive.
    /// </summary>
    /// <param name="input">The candidate phone number string.</param>
    /// <returns>True if the number is valid; otherwise false.</returns>
    private static bool IsValidPhoneNumber(string input)
    {
        foreach (var c in input)
        {
            if (!char.IsDigit(c) && c != '+' && c != '#' && c != '*')
                return false;
        }
        return input.Length > 0 && input.Length <= 50;
    }

    /// <summary>
    /// Clears the console and draws the Dialex banner: a phone ASCII-art
    /// keypad followed by a boxed header with the version string.
    /// </summary>
    private static void ShowBanner()
    {
        Console.Clear();
        var pad = new string(' ', 8);
        Console.WriteLine($"{pad}     ┌──────────┐");
        Console.WriteLine($"{pad}     │  ┌────┐  │");
        Console.WriteLine($"{pad}     │  │ ○○ │  │");
        Console.WriteLine($"{pad}     │  └────┘  │");
        Console.WriteLine($"{pad}     │ ──────── │");
        Console.WriteLine($"{pad}     │ 1  2  3  │");
        Console.WriteLine($"{pad}     │ 4  5  6  │");
        Console.WriteLine($"{pad}     │ 7  8  9  │");
        Console.WriteLine($"{pad}     │ *  0  #  │");
        Console.WriteLine($"{pad}     └────┬─────┘");
        Console.WriteLine($"{pad}    ──────┴───────");
        Console.WriteLine($"╔══════════════════════════════════════╗");
        Console.WriteLine($"║  Dialex v{Version,-28}║");
        Console.WriteLine($"║  SIP Softphone for .NET 4.8         ║");
        Console.WriteLine($"╚══════════════════════════════════════╝");
        Console.WriteLine();
    }

    /// <summary>
    /// Interactive account-setup wizard.  Prompts the user for every SIP
    /// account field (server, proxy, domain, port, extension, display name,
    /// auth username, auth password).  Defaults are offered for optional
    /// fields.  Returns null if a required field is left empty.
    /// </summary>
    /// <returns>A new <see cref="SipAccount"/> instance, or null on abort.</returns>
    private static SipAccount PromptAccount()
    {
        Console.WriteLine("Enter your SIP account details (press Enter to skip optional fields):");
        Console.Write("Server (e.g. pbx.domain.com): ");
        var server = Console.ReadLine()?.Trim();
        if (string.IsNullOrWhiteSpace(server)) { Console.WriteLine("Server is required."); return null; }

        Console.Write("Proxy [{0}]: ", server);
        var proxy = Console.ReadLine()?.Trim();
        if (string.IsNullOrWhiteSpace(proxy)) proxy = server;

        Console.Write("Domain [{0}]: ", server);
        var domain = Console.ReadLine()?.Trim();
        if (string.IsNullOrWhiteSpace(domain)) domain = server;

        Console.Write("Port [5060]: ");
        var portStr = Console.ReadLine()?.Trim();
        if (!int.TryParse(portStr, out var port) || port <= 0) port = 5060;

        Console.Write("Extension: ");
        var ext = Console.ReadLine()?.Trim();
        if (string.IsNullOrWhiteSpace(ext)) { Console.WriteLine("Extension is required."); return null; }

        Console.Write("Display Name (optional): ");
        var display = Console.ReadLine()?.Trim();

        Console.Write("Auth Username [{0}]: ", ext);
        var user = Console.ReadLine()?.Trim();
        if (string.IsNullOrWhiteSpace(user)) user = ext;

        Console.Write("Auth Password: ");
        var pass = ReadPassword();
        Console.WriteLine();
        if (string.IsNullOrWhiteSpace(pass)) { Console.WriteLine("Password is required."); return null; }

        return new SipAccount
        {
            Server = server, Proxy = proxy, Domain = domain,
            Port = port, Extension = ext, DisplayName = display ?? "",
            Username = user, Password = pass
        };
    }

    /// <summary>
    /// Handles the "account" (acc) command with the following sub-commands:
    ///   list (l)     – display all accounts with index, status, and default marker.
    ///   add (a)      – launch the interactive wizard and persist the new account.
    ///   remove (r)   – delete the account at the given index; switch default if needed.
    ///   edit (e)     – update fields of the account at the given index in-place.
    ///   default (d)  – set the account at the given index as the active default.
    /// </summary>
    /// <param name="parts">The split command parts (parts[0]=="account", parts[1]=sub-command).</param>
    private static void HandleAccountCommand(string[] parts)
    {
        _accountManager.Load();
        var accounts = _accountManager.Accounts;

        var sub = parts.Length > 1 ? parts[1].ToLower() : "";
        if (parts.Length == 1 || sub == "list" || sub == "l")
        {
            // ── list / l ──────────────────────────────────────────────────────
            // Display all accounts in a table with index, online/offline status,
            // and a right-arrow (⇨) on the default entry.
            if (accounts.Count == 0)
            {
                Console.WriteLine("No accounts configured. Use 'acc a' to add one.");
                return;
            }
            Console.WriteLine($"{"#",-3} {"Status",-9} Account");
            Console.WriteLine(new string('-', 56));
            for (int i = 0; i < accounts.Count; i++)
            {
                var label = accounts[i].DisplayLabel;
                var isDef = i == _accountManager.DefaultIndex;
                string status;
                if (isDef && _sipService?.IsOnline == true)
                    status = "ONLINE";
                else if (isDef)
                    status = "OFFLINE";
                else
                    status = "-";
                var marker = isDef ? "⇨ " : "  ";
                Console.WriteLine($"{i,-3} {status,-9} {marker}{label}");
            }
            return;
        }

        if (sub == "add" || sub == "a")
        {
            // ── add / a ───────────────────────────────────────────────────────
            // Run the interactive account wizard and persist the new account.
            var acc = PromptAccount();
            if (acc != null)
            {
                _accountManager.Add(acc);
                CliHelper.Success($"Account added: {acc.DisplayLabel}");
            }
            return;
        }

        if ((sub == "remove" || sub == "r") && parts.Length > 2 && int.TryParse(parts[2], out var rmIdx))
        {
            // ── remove / r <idx> ──────────────────────────────────────────────
            // Remove the account and, if it was the default, either switch to
            // another account or shut down the SIP service entirely.
            if (rmIdx < 0 || rmIdx >= accounts.Count)
            { Console.WriteLine("Invalid index."); return; }
            var label = accounts[rmIdx].DisplayLabel;
            var wasDefault = rmIdx == _accountManager.DefaultIndex;
            _accountManager.Remove(rmIdx);
            CliHelper.Info($"Removed account #{rmIdx}: {label}");
            _sipService?.CancelReconnect();
            if (wasDefault || _accountManager.Accounts.Count == 0)
            {
                _sipService?.Shutdown();
                CliHelper.Info("No active account. Use 'acc a' or 'acc d <idx>' to register.");
            }
            else
            {
                _sipService?.SwitchAccount(_accountManager.Accounts[_accountManager.DefaultIndex]);
            }
            return;
        }

        if ((sub == "edit" || sub == "e") && parts.Length > 2 && int.TryParse(parts[2], out var editIdx))
        {
            // ── edit / e <idx> ────────────────────────────────────────────────
            // Prompt for each field, showing the current value as the default.
            // If the default account is edited, immediately switch to the new config.
            if (editIdx < 0 || editIdx >= accounts.Count)
            { Console.WriteLine("Invalid index."); return; }
            var old = accounts[editIdx];
            Console.WriteLine($"Editing account #{editIdx}: {old.DisplayLabel}");
            Console.Write("Server [{0}]: ", old.Server);
            var s = Console.ReadLine()?.Trim(); if (!string.IsNullOrWhiteSpace(s)) old.Server = s;
            Console.Write("Proxy [{0}]: ", old.Proxy);
            s = Console.ReadLine()?.Trim(); if (!string.IsNullOrWhiteSpace(s)) old.Proxy = s;
            Console.Write("Domain [{0}]: ", old.Domain);
            s = Console.ReadLine()?.Trim(); if (!string.IsNullOrWhiteSpace(s)) old.Domain = s;
            Console.Write("Port [{0}]: ", old.Port);
            s = Console.ReadLine()?.Trim(); if (int.TryParse(s, out var p)) old.Port = p;
            Console.Write("Extension [{0}]: ", old.Extension);
            s = Console.ReadLine()?.Trim(); if (!string.IsNullOrWhiteSpace(s)) old.Extension = s;
            Console.Write("Display Name [{0}]: ", old.DisplayName);
            s = Console.ReadLine()?.Trim(); if (s != null) old.DisplayName = s;
            Console.Write("Auth Username [{0}]: ", old.Username);
            s = Console.ReadLine()?.Trim(); if (!string.IsNullOrWhiteSpace(s)) old.Username = s;
            Console.Write("Auth Password (leave blank to keep): ");
            var pw = ReadPassword();
            Console.WriteLine();
            if (!string.IsNullOrWhiteSpace(pw)) old.Password = pw;
            _accountManager.Save();
            CliHelper.Success($"Account #{editIdx} updated.");
            if (editIdx == _accountManager.DefaultIndex)
                _sipService?.SwitchAccount(old);
            return;
        }

        if ((sub == "default" || sub == "d") && parts.Length > 2 && int.TryParse(parts[2], out var defIdx))
        {
            // ── default / d <idx> ─────────────────────────────────────────────
            // Mark the account as the default and immediately switch the SIP
            // service to use its credentials.
            if (defIdx < 0 || defIdx >= accounts.Count)
            { Console.WriteLine("Invalid index."); return; }
            _accountManager.SetDefault(defIdx);
            CliHelper.Success($"Default account changed to: {accounts[defIdx].DisplayLabel}");
            _sipService?.SwitchAccount(accounts[defIdx]);
            return;
        }

        Console.WriteLine("Usage: account list (l) | account add (a) | account remove (r) <idx> | account edit (e) <idx> | account default (d) <idx>");
    }
}
