using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using AssetShelf.Look;

namespace AssetShelf;

/// <summary>A library shared from a file server (SMB, e.g. a NAS): the server's own accounts decide who may read it and
/// who may change it. Connecting saves the password where the system keeps them (Windows Credential Manager; the
/// keyring through secret-tool on Linux) and points Packs and Index at the share's packs/ and index/.</summary>
public static class Shared
{
    /// <summary>\\host\share\folder, //host/share/folder or smb://host/share/folder → its parts; null when it is none of them.</summary>
    public static (string Host, string Share, string Folder)? Parse(string address)
    {
        string a = address.Trim();
        a = a.StartsWith("smb://", StringComparison.OrdinalIgnoreCase) ? a[6..] : a.TrimStart('\\', '/');
        string[] p = a.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        return p.Length < 2 || p[0].Contains('@') ? null : (p[0], p[1], string.Join('/', p[2..]));
    }

    /// <summary>Connects and returns the local path of the library folder (holding packs/ and index/).</summary>
    public static string Connect(string address, string user, string password)
    {
        var (host, share, rest) = Parse(address) ?? throw new InvalidOperationException($"Not a share address: {address} (write it as \\\\server\\share\\folder)");
        string root;
        if (OperatingSystem.IsWindows())
        {
            SaveWindowsCredential(host, user, password);
            ConnectWindows($@"\\{host}\{share}", user, password);
            root = $@"\\{host}\{share}" + (rest.Length > 0 ? @"\" + rest.Replace('/', '\\') : "");
        }
        else if (OperatingSystem.IsLinux())
        {
            root = GioMount(host, share, user, password);
            StoreKeyring(host, user, password);
            root = rest.Length > 0 ? Path.Combine(root, rest) : root;
        }
        else
        {
            // ponytail: macOS connects through Finder (Go > Connect to Server); add a mount here when someone uses it
            throw new InvalidOperationException("Connect to the share in Finder (Go > Connect to Server), then set Packs and Index in settings.json.");
        }

        string[] found;
        try
        {
            found = [.. Directory.EnumerateDirectories(root).Select(Path.GetFileName)!];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"Connected, but {root} cannot be read: {e.Message}");
        }

        if (!found.Contains("packs", StringComparer.OrdinalIgnoreCase) || !found.Contains("index", StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Connected, but {root} has no packs and index folders (it has: {string.Join(", ", found.Take(8))}; check the folder in the address).");
        }

        return root;
    }

    /// <summary>At start: connects the share again when the library is missing, with the saved password (Linux: the
    /// keyring and a gvfs mount; Windows: Asset Shelf's entry in Credential Manager).</summary>
    public static void Reconnect(Settings s)
    {
        if (s.Share.Length == 0 || Directory.Exists(s.Packs) || Parse(s.Share) is not { } a)
        {
            return;
        }

        try
        {
            if (OperatingSystem.IsWindows())
            {
                if (ReadWindowsPassword(a.Host, s.ShareUser) is { } wpw)
                {
                    ConnectWindows($@"\\{a.Host}\{a.Share}", s.ShareUser, wpw);
                }

                return;
            }

            if (!OperatingSystem.IsLinux())
            {
                return;
            }

            if (RunText("secret-tool", null, "lookup", "service", "asset-shelf", "account", KeyringAccount(a.Host, s.ShareUser)) is (0, { Length: > 0 } pw))
            {
                GioMount(a.Host, a.Share, s.ShareUser, pw);
            }
        }
        catch (InvalidOperationException e)
        {
            Log.Write("shared", $"reconnect failed: {e.Message}");
        }
    }

    /// <summary>True when this user may write into <paramref name="dir"/>. A shared library is read-only for its
    /// members: Sync and indexing are then left to its owner.</summary>
    public static bool Writable(string dir)
    {
        string probe = Path.Combine(dir, $".asset-shelf-write-{Environment.ProcessId}");
        try
        {
            using (File.Create(probe, 1, FileOptions.DeleteOnClose))
            {
            }

            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string KeyringAccount(string host, string user) => $"smb:{user}@{host}";

    private static void StoreKeyring(string host, string user, string password)
    {
        // the password goes on stdin: a command line can be read by other processes while it runs
        if (RunText("secret-tool", password, "store", "--label", $"Asset Shelf: {user} on {host}", "service", "asset-shelf", "account", KeyringAccount(host, user)) is not (0, _))
        {
            Log.Write("shared", "the keyring (secret-tool) did not take the password: connect again after a restart");
        }
    }

    /// <summary>gio mount (GNOME's gvfs with its SMB backend): the share appears under $XDG_RUNTIME_DIR/gvfs.</summary>
    private static string GioMount(string host, string share, string user, string password)
    {
        string gvfs = Path.Combine(Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") ?? $"/run/user/{Environment.GetEnvironmentVariable("UID")}", "gvfs");
        string? Mounted() => Directory.Exists(gvfs)
            ? Directory.EnumerateDirectories(gvfs).FirstOrDefault(d => Path.GetFileName(d).StartsWith("smb-share:", StringComparison.Ordinal)
                && Path.GetFileName(d).Contains($"server={host}", StringComparison.OrdinalIgnoreCase)
                && Path.GetFileName(d).Contains($"share={share}", StringComparison.OrdinalIgnoreCase))
            : null;
        if (Mounted() is { } already)
        {
            return already;
        }

        // user and domain in the address, so gio asks only for the password, read from stdin
        // ponytail: not tried on a desktop with gvfs-smb yet (the one it was written on has none)
        var (_, said) = RunText("gio", password + "\n", "mount", $"smb://WORKGROUP;{Uri.EscapeDataString(user)}@{host}/{share}")
            ?? throw new InvalidOperationException("Mounting a share needs gio with SMB support (packages gvfs and gvfs-smb), or mount it yourself and set Packs and Index in settings.json.");
        return Mounted() ?? throw new InvalidOperationException($"Could not mount {share} on {host} ({said.Trim()}). It needs GNOME's gvfs-smb; or mount the share yourself and set Packs and Index in settings.json.");
    }

    /// <summary>Runs a command with <paramref name="stdin"/>: its exit code and output (stdout, then stderr; one line's
    /// end trimmed). Null when it cannot start.</summary>
    private static (int Exit, string Output)? RunText(string exe, string? stdin, params string[] args)
    {
        var psi = new ProcessStartInfo(exe) { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (string a in args)
        {
            psi.ArgumentList.Add(a);
        }

        try
        {
            using Process p = Process.Start(psi)!;
            p.StandardInput.Write(stdin ?? "");
            p.StandardInput.Close();
            Task<string> err = p.StandardError.ReadToEndAsync();
            string output = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(60_000))
            {
                p.Kill();
                return (-1, "timed out");
            }

            return (p.ExitCode, (output + err.Result).TrimEnd('\n'));
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    // ---- Windows Credential Manager (what "cmdkey /add" does, without the password on a command line) ----

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public int Flags;
        public int Type;
        public string TargetName;
        public string? Comment;
        public long LastWritten;
        public int CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist;
        public int AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWrite(ref Credential credential, int flags);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NetResource
    {
        public int Scope, Type, DisplayType, Usage;
        public string? LocalName;
        public string RemoteName;
        public string? Comment, Provider;
    }

    [DllImport("mpr.dll", EntryPoint = "WNetAddConnection2W", CharSet = CharSet.Unicode)]
    private static extern int WNetAddConnection2(ref NetResource resource, string password, string user, int flags);

    [DllImport("mpr.dll", EntryPoint = "WNetCancelConnection2W", CharSet = CharSet.Unicode)]
    private static extern int WNetCancelConnection2(string name, int flags, bool force);

    /// <summary>Opens the share with this account now (what "net use" does), so a wrong password or an unreachable
    /// server is reported as such instead of the folder just looking missing.</summary>
    private static void ConnectWindows(string unc, string user, string password)
    {
        var r = new NetResource { Type = 1, RemoteName = unc };  // RESOURCETYPE_DISK
        int err = WNetAddConnection2(ref r, password, user, 0);
        if (err == 1219)
        {
            // ERROR_SESSION_CREDENTIAL_CONFLICT: Windows already talks to this server under another login; drop it once
            WNetCancelConnection2(unc, 0, true);
            err = WNetAddConnection2(ref r, password, user, 0);
        }

        if (err is not (0 or 85))  // 85: already connected
        {
            throw new InvalidOperationException($"Windows could not open {unc} as {user}: {new System.ComponentModel.Win32Exception(err).Message} (error {err}).");
        }
    }

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(string target, int type, int flags, out IntPtr credential);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);

    private static string GenericTarget(string host, string user) => $"Asset Shelf: {user}@{host}";

    private static string? ReadWindowsPassword(string host, string user)
    {
        if (!CredRead(GenericTarget(host, user), 1, 0, out IntPtr p))
        {
            return null;
        }

        try
        {
            var c = Marshal.PtrToStructure<Credential>(p);
            return Marshal.PtrToStringUni(c.CredentialBlob, c.CredentialBlobSize / 2);
        }
        finally
        {
            CredFree(p);
        }
    }

    private static void SaveWindowsCredential(string host, string user, string password)
    {
        byte[] blob = Encoding.Unicode.GetBytes(password);
        IntPtr p = Marshal.AllocHGlobal(blob.Length);
        try
        {
            Marshal.Copy(blob, 0, p, blob.Length);
            // CRED_TYPE_DOMAIN_PASSWORD (2) is what Windows file sharing looks up; CRED_PERSIST_ENTERPRISE (3) keeps it
            var c = new Credential { Type = 2, TargetName = host, UserName = user, CredentialBlob = p, CredentialBlobSize = blob.Length, Persist = 3 };
            // a domain password cannot be read back, so the same password also goes in a generic entry (CRED_TYPE_GENERIC,
            // 1) that Asset Shelf reads at start to connect again
            var g = new Credential { Type = 1, TargetName = GenericTarget(host, user), UserName = user, CredentialBlob = p, CredentialBlobSize = blob.Length, Persist = 3 };
            if (!CredWrite(ref c, 0) || !CredWrite(ref g, 0))
            {
                throw new InvalidOperationException($"Windows did not save the password (error {Marshal.GetLastWin32Error()}).");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(p);
        }
    }
}

/// <summary>Asks for the share's address, the user name and the password.</summary>
public sealed class ConnectDialog : Window
{
    public static Task<(string Address, string User, string Password)?> Ask(Window owner, string address, string user) =>
        new ConnectDialog(address, user).ShowDialog<(string, string, string)?>(owner);

    private ConnectDialog(string address, string user)
    {
        Title = "Connect to a shared library";
        Width = 440;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Look.Theme.SurfacePage;
        TextBox Box(string text, string hint, char pw = default) => new() { Text = text, PlaceholderText = hint, PasswordChar = pw, FontSize = Look.Theme.Body };
        TextBox addr = Box(address, @"\\server\share\folder"), name = Box(user, "User name"), pass = Box("", "Password", '•');
        Button cancel = Chrome.TextButton("Cancel", "Close without connecting.");
        Button ok = Chrome.PrimaryButton("check", "Connect", "Connect and use this library.");
        cancel.Click += (_, _) => Close(null);
        ok.Click += (_, _) =>
        {
            if (Shared.Parse(addr.Text ?? "") is not null && (name.Text ?? "").Trim().Length > 0)
            {
                Close(((addr.Text ?? "").Trim(), (name.Text ?? "").Trim(), pass.Text ?? ""));
            }
        };
        Label Caption(string t) => new() { Content = t, FontSize = Look.Theme.Label, Foreground = Look.Theme.TextSecondary, Padding = new Thickness(0) };
        Content = new StackPanel
        {
            Margin = new Thickness(16),
            Spacing = 6,
            Children =
            {
                new TextBlock { Text = "The library folder on the file server (it holds packs and index), and your account there. The password is saved by the system, not by Asset Shelf.", FontSize = Look.Theme.Label, Foreground = Look.Theme.TextSecondary, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                Caption("Address"), addr, Caption("User name"), name, Caption("Password"), pass,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 6, 0, 0), Children = { cancel, ok } },
            },
        };
        Opened += (_, _) => (address.Length == 0 ? addr : pass).Focus();
    }
}
