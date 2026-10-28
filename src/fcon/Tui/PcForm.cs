namespace FleetConnect.Tui;

internal enum PcFormMode
{
    Add,
    Edit,
    Clone,
}

internal enum PcFormAction
{
    None,
    Saved,
    Cancelled,
}

// Inline form state for adding, editing, or cloning a machine in the right-hand
// inspector panel without leaving the alternate screen.
internal sealed class PcForm
{
    public const int FieldName = 0;
    public const int FieldHost = 1;
    public const int FieldProtocol = 2;
    public const int FieldUser = 3;
    public const int FieldSshAlias = 4;
    public const int FieldPort = 5;
    public const int FieldNote = 6;
    public const int FieldSave = 7;
    public const int FieldCount = 8;

    public PcFormMode Mode { get; }
    public string OriginalName { get; }

    public string Name = "";
    public string Address = "";
    public PcProtocol Protocol = PcProtocol.Rdp;
    public string User = "";
    public string SshAlias = "";
    public string Port = "";
    public string Note = "";

    public int SelectedField;
    public string EditingBuffer;
    public string Error = "";

    readonly List<string> localAliases;

    public bool IsEditingText => EditingBuffer != null;

    public PcForm(PcFormMode mode, PcModel seed, IReadOnlyList<string> aliases)
    {
        Mode = mode;
        localAliases = aliases != null ? aliases.ToList() : new List<string>();
        if (seed != null)
        {
            OriginalName = mode == PcFormMode.Edit ? seed.Name : null;
            Name = mode == PcFormMode.Clone ? seed.Name + "-copy" : seed.Name;
            Address = seed.Address;
            Protocol = seed.Protocol;
            User = seed.User;
            SshAlias = seed.SshAlias;
            Port = seed.Port;
            Note = seed.Note;
        }
        SelectedField = FieldName;
        if (mode == PcFormMode.Add || mode == PcFormMode.Clone)
            EditingBuffer = Name;
    }

    public string Title => Mode switch
    {
        PcFormMode.Add => "add machine",
        PcFormMode.Clone => "clone machine",
        _ => "edit " + (OriginalName ?? Name),
    };

    public static string FieldLabel(int index) => index switch
    {
        FieldName => "Name",
        FieldHost => "Host",
        FieldProtocol => "Protocol",
        FieldUser => "User",
        FieldSshAlias => "SSH alias",
        FieldPort => "Port",
        FieldNote => "Note",
        FieldSave => "[ Save machine ]",
        _ => "",
    };

    public string GetFieldValue(int index) => index switch
    {
        FieldName => Name,
        FieldHost => Address,
        FieldProtocol => Protocols.ToName(Protocol),
        FieldUser => User,
        FieldSshAlias => SshAlias,
        FieldPort => Port,
        FieldNote => Note,
        _ => "",
    };

    void SetFieldValue(int index, string value)
    {
        string v = value ?? "";
        switch (index)
        {
            case FieldName: Name = v.Trim(); break;
            case FieldHost: Address = v.Trim(); break;
            case FieldUser: User = v.Trim(); break;
            case FieldSshAlias: SshAlias = v.Trim(); break;
            case FieldPort: Port = v.Trim(); break;
            case FieldNote: Note = v.Trim(); break;
        }
    }

    public PcFormAction HandleKey(ConsoleKeyInfo key, IReadOnlyList<PcModel> existing,
                                  out PcModel savedModel)
    {
        savedModel = null;
        Error = "";

        // Ctrl+S saves from anywhere in the form, committing active text first.
        if (key.Key == ConsoleKey.S && key.Modifiers.HasFlag(ConsoleModifiers.Control))
        {
            if (EditingBuffer != null)
            {
                SetFieldValue(SelectedField, EditingBuffer);
                EditingBuffer = null;
            }
            return TryComplete(existing, out savedModel);
        }

        if (EditingBuffer != null)
        {
            switch (key.Key)
            {
                case ConsoleKey.Enter:
                    SetFieldValue(SelectedField, EditingBuffer);
                    EditingBuffer = null;
                    return PcFormAction.None;
                case ConsoleKey.Tab:
                    SetFieldValue(SelectedField, EditingBuffer);
                    EditingBuffer = null;
                    MoveSelection(key.Modifiers.HasFlag(ConsoleModifiers.Shift) ? -1 : 1);
                    return PcFormAction.None;
                case ConsoleKey.Escape:
                    EditingBuffer = null;
                    return PcFormAction.None;
                case ConsoleKey.Delete:
                case ConsoleKey.Backspace when key.Modifiers.HasFlag(ConsoleModifiers.Control):
                    EditingBuffer = "";
                    return PcFormAction.None;
                case ConsoleKey.Backspace:
                    if (EditingBuffer.Length > 0)
                        EditingBuffer = EditingBuffer.Substring(0, EditingBuffer.Length - 1);
                    return PcFormAction.None;
                default:
                    if (!char.IsControl(key.KeyChar))
                        EditingBuffer += key.KeyChar;
                    return PcFormAction.None;
            }
        }

        switch (key.Key)
        {
            case ConsoleKey.Escape:
                return PcFormAction.Cancelled;
            case ConsoleKey.UpArrow:
                MoveSelection(-1);
                return PcFormAction.None;
            case ConsoleKey.DownArrow:
                MoveSelection(1);
                return PcFormAction.None;
            case ConsoleKey.Tab:
                MoveSelection(key.Modifiers.HasFlag(ConsoleModifiers.Shift) ? -1 : 1);
                return PcFormAction.None;
            case ConsoleKey.LeftArrow:
                StepChoice(-1);
                return PcFormAction.None;
            case ConsoleKey.RightArrow:
                StepChoice(1);
                return PcFormAction.None;
            case ConsoleKey.Spacebar when SelectedField == FieldProtocol:
                StepChoice(1);
                return PcFormAction.None;
            case ConsoleKey.Delete:
                if (SelectedField != FieldProtocol && SelectedField != FieldSave)
                    SetFieldValue(SelectedField, "");
                return PcFormAction.None;
            case ConsoleKey.Enter:
                if (SelectedField == FieldSave)
                    return TryComplete(existing, out savedModel);
                if (SelectedField == FieldProtocol)
                {
                    StepChoice(1);
                    return PcFormAction.None;
                }
                EditingBuffer = GetFieldValue(SelectedField);
                return PcFormAction.None;
            default:
                return PcFormAction.None;
        }
    }

    void MoveSelection(int delta)
    {
        SelectedField = (SelectedField + delta + FieldCount) % FieldCount;
    }

    void StepChoice(int delta)
    {
        if (SelectedField == FieldProtocol)
        {
            Protocol = Protocol == PcProtocol.Rdp ? PcProtocol.Ssh : PcProtocol.Rdp;
            return;
        }
        if (SelectedField == FieldSshAlias && localAliases.Count > 0)
        {
            var options = new List<string> { "" };
            foreach (string a in localAliases)
            {
                if (!options.Contains(a))
                    options.Add(a);
            }
            int idx = options.FindIndex(o => string.Equals(o, SshAlias, StringComparison.Ordinal));
            if (idx < 0)
            {
                options.Add(SshAlias);
                idx = options.Count - 1;
            }
            idx = (idx + delta + options.Count) % options.Count;
            SshAlias = options[idx];
        }
    }

    PcFormAction TryComplete(IReadOnlyList<PcModel> existing, out PcModel savedModel)
    {
        if (!Validate(existing, out savedModel, out string err))
        {
            Error = err;
            return PcFormAction.None;
        }
        return PcFormAction.Saved;
    }

    public bool Validate(IReadOnlyList<PcModel> existing, out PcModel built, out string error)
    {
        built = null;
        error = "";
        string cleanName = (Name ?? "").Trim();
        string cleanHost = (Address ?? "").Trim();
        string cleanUser = (User ?? "").Trim();
        string cleanAlias = (SshAlias ?? "").Trim();
        string cleanPort = (Port ?? "").Trim();
        string cleanNote = (Note ?? "").Trim();

        if (cleanName == "")
        {
            error = "Name is required.";
            return false;
        }
        if (existing != null && existing.Any(p =>
                string.Equals(p.Name, cleanName, StringComparison.Ordinal) &&
                !string.Equals(p.Name, OriginalName, StringComparison.Ordinal)))
        {
            error = "A machine named '" + cleanName + "' already exists.";
            return false;
        }
        if (cleanHost == "" && cleanAlias == "")
        {
            error = "Specify a Host address or an SSH alias.";
            return false;
        }
        if (cleanPort != "")
        {
            if (!int.TryParse(cleanPort, out int portNum) || portNum < 1 || portNum > 65535)
            {
                error = "Port must be 1..65535 (or empty for default).";
                return false;
            }
        }

        built = new PcModel
        {
            Name = cleanName,
            Address = cleanHost,
            Protocol = Protocol,
            User = cleanUser,
            SshAlias = cleanAlias,
            Port = cleanPort,
            Note = cleanNote,
        };
        return true;
    }
}
