// pcs.csv storage. Same columns and same tolerance as the PS version:
// a missing file answers an empty list, a row without Name+Host is skipped,
// a missing column reads as blank, a protocol typo warns and falls back.
using System.Text;

namespace FleetConnect;

static class CsvStore
{
    static readonly string[] Columns =
        { "Name", "Host", "Protocol", "User", "SshAlias", "Port", "Note" };

    public static string GetListPath()
    {
        string custom = Environment.GetEnvironmentVariable("FLEET_CONNECT_CSV");
        if (!string.IsNullOrWhiteSpace(custom))
            return custom;
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(appData, "fleet-connect", "pcs.csv");
    }

    public static string GetListDirectory() => Path.GetDirectoryName(GetListPath());

    public static List<PcModel> ReadPcList()
    {
        var list = new List<PcModel>();
        string path = GetListPath();
        if (!File.Exists(path))
            return list;
        var rows = ParseFile(path);
        foreach (var row in rows)
        {
            string name = Field(row, "Name");
            string host = Field(row, "Host");
            if (name == "" && host == "")
                continue;
            var pc = new PcModel
            {
                Name = name != "" ? name : host,
                Address = host,
                User = Field(row, "User"),
                SshAlias = Field(row, "SshAlias"),
                Port = Field(row, "Port"),
                Note = Field(row, "Note"),
            };
            try
            {
                pc.Protocol = Protocols.Parse(Field(row, "Protocol"));
            }
            catch (Exception ex)
            {
                // A typo in one row must not hide the other machines.
                Render.Note(pc.Name + ": " + ex.Message + " Falling back to rdp.");
                pc.Protocol = PcProtocol.Rdp;
            }
            list.Add(pc);
        }
        return list;
    }

    public static void WritePcList(List<PcModel> pcs)
    {
        string path = GetListPath();
        string dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        if (File.Exists(path))
            File.Copy(path, path + ".bak", overwrite: true);
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",", Columns));
        foreach (var pc in pcs)
        {
            sb.AppendLine(string.Join(",", new[]
            {
                Escape(pc.Name),
                Escape(pc.Address),
                Escape(Protocols.ToName(pc.Protocol) == "ssh" ? "Ssh" : "Rdp"),
                Escape(pc.User),
                Escape(pc.SshAlias),
                Escape(pc.Port),
                Escape(pc.Note),
            }));
        }
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    public static string NewEmptyList()
    {
        string path = GetListPath();
        string dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        File.WriteAllText(path, "Name,Host,Protocol,User,SshAlias,Port,Note\n",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }

    static string Field(Dictionary<string, string> row, string name)
    {
        if (row.TryGetValue(name, out string value) && value != null)
            return value.Trim();
        return "";
    }

    static List<Dictionary<string, string>> ParseFile(string path)
    {
        string text = File.ReadAllText(path);
        var rows = new List<Dictionary<string, string>>();
        var record = new List<string>();
        var cell = new StringBuilder();
        bool inQuotes = false;
        bool headerDone = false;
        string[] header = Array.Empty<string>();

        void EndCell()
        {
            record.Add(cell.ToString());
            cell.Clear();
        }

        void EndRecord()
        {
            EndCell();
            if (!headerDone)
            {
                header = record.ToArray();
                headerDone = true;
            }
            else
            {
                var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < record.Count; i++)
                {
                    string key = i < header.Length ? header[i].Trim() : ("Column" + (i + 1));
                    dict[key] = record[i];
                }
                rows.Add(dict);
            }
            record.Clear();
        }

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        cell.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    cell.Append(c);
                }
            }
            else if (c == '"')
            {
                inQuotes = true;
            }
            else if (c == ',')
            {
                EndCell();
            }
            else if (c == '\r')
            {
                // Swallow; the \n ends the record.
            }
            else if (c == '\n')
            {
                EndRecord();
            }
            else
            {
                cell.Append(c);
            }
        }
        // Trailing record without a final newline.
        if (cell.Length > 0 || record.Count > 0)
            EndRecord();
        return rows;
    }

    static string Escape(string value)
    {
        if (value == null)
            return "";
        if (value.IndexOfAny(new[] { ',', '"', '\r', '\n' }) < 0)
            return value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
}
