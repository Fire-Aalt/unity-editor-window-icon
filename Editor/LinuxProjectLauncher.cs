#if UNITY_EDITOR_LINUX
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace BiteMe.EditorWindowIcon
{
    static class LinuxProjectLauncher
    {
        const string OwnershipKey = "X-BiteMe-EditorWindowIcon=true";
        const string LaunchWaitSeconds = "30";

        // The outer flock serializes shortcut launches until unity open --wait exits. Check
        // Unity's own lock as well, since the Editor may have been opened from Hub or a terminal.
        // Open the lock read-only: Unity can remove it while the previous process is closing.
        const string LaunchProject = @"cli=$1
editor=$2
project=$3
flock_bin=$4
wait_seconds=$5
lock_file=""$project/Temp/UnityLockfile""
if [ -f ""$lock_file"" ]; then
    (""$flock_bin"" -s -w ""$wait_seconds"" 9) 9<""$lock_file"" || [ ! -e ""$lock_file"" ] || exit 0
fi
exec ""$cli"" --non-interactive open --wait --editor-path ""$editor"" ""$project""";

        public static readonly string Id = "com.biteme.unityeditor.project" + Hash128.Compute(ProjectIcon.Root);
        static string PreferenceKey => Id + ".Launcher";
        static string EntryPath => Path.Combine(DataHome(), "applications", Id + ".desktop");
        static string IconFolder => Path.Combine(DataHome(), "biteme-editor-window-icon", Id);

        public static bool Enabled
        {
            get => EditorPrefs.GetBool(PreferenceKey, true);
            set => EditorPrefs.SetBool(PreferenceKey, value);
        }

        public static string Publish(byte[] png)
        {
            string entryPath = EntryPath;
            string previous = File.Exists(entryPath) ? File.ReadAllText(entryPath) : null;
            if (previous != null && !IsOwned(previous))
                throw new IOException("A desktop entry with the same ID already exists.");

            string editor = EditorApplication.applicationPath;
            if (!Path.IsPathRooted(editor))
                throw new IOException("The Unity Editor executable path is unavailable.");

            string project = ProjectIcon.Root;
            string cli = ResolveCommand("unity");
            string flock = ResolveCommand("flock");

            string iconPath = Path.Combine(IconFolder, Hash128.Compute(png) + ".png");
            if (!File.Exists(iconPath))
                WriteAtomically(iconPath, png);

            string exec = QuoteArgument(flock) + " -o -w " + LaunchWaitSeconds + " -E 0 " + QuoteArgument(project) +
                " /bin/sh -c " + QuoteArgument(LaunchProject) + " unity-editor-window-icon " +
                QuoteArgument(cli) + " " + QuoteArgument(editor) + " " + QuoteArgument(project) + " " +
                QuoteArgument(flock) + " " + LaunchWaitSeconds;
            string entry = "[Desktop Entry]\n" +
                "Type=Application\n" +
                "Name=" + EscapeValue("Unity - " + Path.GetFileName(project)) + "\n" +
                "Exec=" + EscapeValue(exec) + "\n" +
                "TryExec=" + EscapeValue(cli) + "\n" +
                "Icon=" + EscapeValue(iconPath) + "\n" +
                "StartupWMClass=" + Id + "\n" +
                "Terminal=false\n" +
                "Categories=Development;\n" +
                OwnershipKey + "\n";

            if (entry != previous)
                WriteAtomically(entryPath, Encoding.UTF8.GetBytes(entry));

            // Publish the entry after the new PNG exists, then remove superseded images.
            DeleteUnusedIcons(iconPath);
            return entryPath;
        }

        public static void Remove()
        {
            string entryPath = EntryPath;
            if (File.Exists(entryPath))
            {
                if (!IsOwned(File.ReadAllText(entryPath)))
                    return;
                File.Delete(entryPath);
            }

            DeleteUnusedIcons(null);
            if (Directory.Exists(IconFolder) && Directory.GetFileSystemEntries(IconFolder).Length == 0)
                Directory.Delete(IconFolder);
        }

        static string DataHome()
        {
            string configured = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
            if (Path.IsPathRooted(configured))
                return configured;

            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!Path.IsPathRooted(home))
                throw new IOException("The user's XDG data directory is unavailable.");
            return Path.Combine(home, ".local", "share");
        }

        static string ResolveCommand(string name)
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string paths = Environment.GetEnvironmentVariable("PATH") + ":" +
                Path.Combine(home, ".local", "bin") + ":/usr/bin:/bin";
            foreach (string folder in paths.Split(':'))
            {
                if (!Path.IsPathRooted(folder) || folder.Contains("="))
                    continue;
                string candidate = Path.Combine(folder, name);
                if (File.Exists(candidate) && access(candidate, 1) == 0) // X_OK
                    return candidate;
            }
            return name;
        }

        static bool IsOwned(string entry) =>
            Array.IndexOf(entry.Split('\n'), OwnershipKey) >= 0;

        static void DeleteUnusedIcons(string current)
        {
            if (!Directory.Exists(IconFolder))
                return;

            foreach (string path in Directory.EnumerateFiles(IconFolder, "*.png"))
            {
                string hash = Path.GetFileNameWithoutExtension(path);
                if (path != current && hash.Length == 32 && Array.TrueForAll(hash.ToCharArray(), Uri.IsHexDigit))
                    File.Delete(path);
            }
        }

        static string QuoteArgument(string value) =>
            "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("$", "\\$")
                .Replace("`", "\\`").Replace("%", "%%") + "\"";

        static string EscapeValue(string value) =>
            value.Replace("\\", "\\\\").Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t");

        static void WriteAtomically(string path, byte[] data)
        {
            string directory = Path.GetDirectoryName(path);
            Directory.CreateDirectory(directory);
            string temporary = Path.Combine(directory, "." + Guid.NewGuid().ToString("N"));
            try
            {
                File.WriteAllBytes(temporary, data);
                if (rename(temporary, path) != 0)
                    throw new IOException($"Could not publish {Path.GetFileName(path)} (errno {Marshal.GetLastWin32Error()}).");
            }
            finally
            {
                File.Delete(temporary);
            }
        }

        [DllImport("libc", SetLastError = true)]
        static extern int rename(string source, string destination);

        [DllImport("libc")]
        static extern int access(string path, int mode);
    }
}
#endif
