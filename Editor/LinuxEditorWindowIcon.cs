#if UNITY_EDITOR_LINUX
using System;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace BiteMe.EditorWindowIcon
{
#if UNITY_6000_5_OR_NEWER
    // Keep the X11 connection and window overrides until the Editor reload or quit callbacks.
    [Unity.Scripting.LifecycleManagement.NoAutoStaticsCleanup]
#endif
    [InitializeOnLoad]
    static class LinuxEditorWindowIcon
    {
        const string LauncherMenu = "Tools/Editor Window Icon/Linux Project Launcher";
        const double CheckInterval = 2;
        const double RetryInterval = 10;
        static readonly int[] IconSizes = { 16, 24, 32, 48, 64, 128 };

        static X11WindowIcons windows;
        static double nextCheck, nextConnection, nextLauncherAttempt;
        static string iconKey, launcherPath;
        static bool launcherDirty = true;
        static bool xcbAvailable = true;
        static bool badgeLoaded;
        static IconPixels badge;
        static byte[] windowIcon, launcherPng;
        static string lastIconError, lastLauncherError;

        static LinuxEditorWindowIcon()
        {
            if (Application.isBatchMode || SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
                return;

            EditorApplication.update += Update;
            AssemblyReloadEvents.beforeAssemblyReload += RestoreWindows;
            EditorApplication.quitting += Disconnect;
        }

        static void Update()
        {
            var now = EditorApplication.timeSinceStartup;
            if (now < nextCheck || EditorApplication.isCompiling || EditorApplication.isUpdating)
                return;
            nextCheck = now + CheckInterval;

            var texture = ProjectIcon.GetDefault();
            var key = texture == null ? "" : $"{AssetDatabase.GetAssetPath(texture)}|{texture.imageContentsHash}";
            if (key != iconKey)
            {
                try
                {
                    Rebuild(texture);
                    iconKey = key;
                    launcherDirty = true;
                    nextLauncherAttempt = 0;
                    lastIconError = null;
                }
                catch (Exception error)
                {
                    WarnOnce(ref lastIconError, "could not build the Linux icon", error);
                    nextCheck = now + RetryInterval;
                    return;
                }
            }

            if (launcherDirty && now >= nextLauncherAttempt)
            {
                nextLauncherAttempt = now + RetryInterval;
                UpdateLauncher();
            }

            UpdateWindows(now);
        }

        static void Rebuild(Texture2D texture)
        {
            if (texture == null)
            {
                windowIcon = launcherPng = null;
                return;
            }

            if (!badgeLoaded)
            {
                badge = LoadBadge();
                badgeLoaded = true;
            }

            var source = IconPixels.FromTexture(texture);
            var elementCount = 0;
            foreach (var size in IconSizes)
                elementCount += 2 + size * size;

            var argb = new uint[elementCount];
            var offset = 0;
            foreach (var size in IconSizes)
            {
                argb[offset++] = (uint)size;
                argb[offset++] = (uint)size;
                foreach (var color in source.Badged(badge, size).Data)
                    argb[offset++] = ((uint)color.a << 24) | ((uint)color.r << 16) | ((uint)color.g << 8) | color.b;
            }

            var png = source.Badged(badge, 256).ToPng();
            windowIcon = new byte[argb.Length * sizeof(uint)];
            Buffer.BlockCopy(argb, 0, windowIcon, 0, windowIcon.Length);
            launcherPng = png;
        }

        static IconPixels LoadBadge()
        {
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            try
            {
                foreach (var file in new[] { "LargeUnityAppIcon.png", "LargeUnityIcon.png" })
                {
                    var path = Path.Combine(EditorApplication.applicationContentsPath, "Resources", file);
                    if (File.Exists(path) && texture.LoadImage(File.ReadAllBytes(path)))
                        return IconPixels.FromTexture(texture);
                }
                return null;
            }
            catch (IOException)
            {
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(texture);
            }
        }

        static void UpdateLauncher()
        {
            try
            {
                if (LinuxProjectLauncher.Enabled && launcherPng != null)
                {
                    launcherPath = LinuxProjectLauncher.Publish(launcherPng);
                }
                else
                {
                    launcherPath = null;
                    LinuxProjectLauncher.Remove();
                }

                lastLauncherError = null;
                launcherDirty = false;
            }
            catch (Exception error)
            {
                WarnOnce(ref lastLauncherError, "could not update the Linux project launcher", error);
            }
        }

        static void UpdateWindows(double now)
        {
            if (windows != null && !windows.Connected)
                Disconnect();

            if (windows == null && windowIcon != null && xcbAvailable && now >= nextConnection)
            {
                nextConnection = now + RetryInterval;
                try
                {
                    windows = X11WindowIcons.Connect();
                }
                catch (DllNotFoundException)
                {
                    xcbAvailable = false;
                }
                catch (EntryPointNotFoundException)
                {
                    xcbAvailable = false;
                }
            }

            var launcherId = launcherPath == null ? null : LinuxProjectLauncher.Id;
            windows?.Update(windowIcon, launcherId, launcherPath);
        }

        [MenuItem(LauncherMenu)]
        static void ToggleLauncher()
        {
            LinuxProjectLauncher.Enabled = !LinuxProjectLauncher.Enabled;
            launcherDirty = true;
            nextCheck = nextLauncherAttempt = 0;
        }

        [MenuItem(LauncherMenu, true)]
        static bool ValidateLauncherMenu()
        {
            Menu.SetChecked(LauncherMenu, LinuxProjectLauncher.Enabled);
            return true;
        }

        static void RestoreWindows()
        {
            try { windows?.Restore(); }
            finally { Disconnect(); }
        }

        static void Disconnect()
        {
            windows?.Dispose();
            windows = null;
        }

        static void WarnOnce(ref string lastMessage, string operation, Exception error)
        {
            if (lastMessage == error.Message)
                return;
            lastMessage = error.Message;
            Debug.LogWarning($"Editor Window Icon: {operation}. {error.Message}");
        }
    }
}
#endif
