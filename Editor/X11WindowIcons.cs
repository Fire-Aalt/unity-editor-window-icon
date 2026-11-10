#if UNITY_EDITOR_LINUX
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace BiteMe.EditorWindowIcon
{
    // A private XCB connection works on both Xorg and XWayland and keeps protocol errors
    // (for example, a window closing between requests) off Unity's own X11 connection.
    sealed class X11WindowIcons : IDisposable
    {
        const string Xcb = "libxcb.so.1";
        const uint Atom = 4, Cardinal = 6, String = 31, Window = 33, WmClass = 67, TransientFor = 68;
        const uint SubstructureEvents = (1 << 19) | (1 << 20);
        const uint IconReadUnits = 512 * 1024; // X11 property lengths are in four-byte units.
        const uint HintReadUnits = 1024;

        readonly IntPtr connection;
        readonly uint root, processId, maxRequestUnits;
        readonly uint clientList, pid, windowType, normal, utility, icon, gtkApplicationId, kdeDesktopFile, utf8;
        readonly uint windowState, skipTaskbar;
        readonly Dictionary<uint, WindowState> overrides = new Dictionary<uint, WindowState>();

        byte[] lastIcon;
        Property iconProperty;

        X11WindowIcons(IntPtr connection, uint root)
        {
            this.connection = connection;
            this.root = root;
            using var process = Process.GetCurrentProcess();
            processId = (uint)process.Id;
            maxRequestUnits = xcb_get_maximum_request_length(connection);

            clientList = Intern("_NET_CLIENT_LIST");
            pid = Intern("_NET_WM_PID");
            windowType = Intern("_NET_WM_WINDOW_TYPE");
            normal = Intern("_NET_WM_WINDOW_TYPE_NORMAL");
            utility = Intern("_NET_WM_WINDOW_TYPE_UTILITY");
            icon = Intern("_NET_WM_ICON");
            gtkApplicationId = Intern("_GTK_APPLICATION_ID");
            kdeDesktopFile = Intern("_KDE_NET_WM_DESKTOP_FILE");
            utf8 = Intern("UTF8_STRING");
            if (IsKWin())
            {
                windowState = Intern("_NET_WM_STATE");
                skipTaskbar = Intern("_NET_WM_STATE_SKIP_TASKBAR");
            }
        }

        public static X11WindowIcons Connect()
        {
            var display = Environment.GetEnvironmentVariable("DISPLAY");
            if (string.IsNullOrEmpty(display) ||
                (!display.StartsWith(":", StringComparison.Ordinal) && !display.StartsWith("unix:", StringComparison.Ordinal)))
                return null;

            var connection = xcb_connect(display, out var screen);
            if (connection == IntPtr.Zero)
                return null;

            var adopted = false;
            try
            {
                if (xcb_connection_has_error(connection) != 0)
                    return null;

                var screens = xcb_setup_roots_iterator(xcb_get_setup(connection));
                while (screen-- > 0 && screens.Remaining > 0)
                    xcb_screen_next(ref screens);
                if (screens.Remaining == 0)
                    return null;

                var root = unchecked((uint)Marshal.ReadInt32(screens.Data));
                var result = new X11WindowIcons(connection, root);
                adopted = true;
                return result;
            }
            finally
            {
                if (!adopted)
                    xcb_disconnect(connection);
            }
        }

        public bool Connected => xcb_connection_has_error(connection) == 0;

        public void Update(byte[] icons, string desktopId, string desktopFile)
        {
            if (icons == null)
            {
                Restore();
                return;
            }

            if (!TryRead(root, clientList, 16384, out var clients) || !Is32Bit(clients, Window))
                return;

            if (icons != lastIcon)
            {
                iconProperty = FitIcons(icons);
                lastIcon = icons;
            }

            var instance = desktopId == null ? null : Encoding.UTF8.GetBytes(desktopId);
            var gtkId = instance == null ? null : new Property(utf8, 8, instance);
            var kdeFile = desktopFile == null ? null : new Property(utf8, 8, Encoding.UTF8.GetBytes(desktopFile));
            var live = new HashSet<uint>();
            for (var i = 0; i < clients.Data.Length; i += sizeof(uint))
            {
                var window = BitConverter.ToUInt32(clients.Data, i);
                if (!Owns(window) || !IsEditorWindow(window))
                    continue;

                live.Add(window);
                if (!overrides.TryGetValue(window, out var state))
                    overrides.Add(window, state = new WindowState());

                Apply(window, icon, IconReadUnits, iconProperty, ref state.Icon);
                ApplyIdentity(window, instance, gtkId, kdeFile, state);
            }

            foreach (var window in overrides.Keys.Where(window => !live.Contains(window)).ToArray())
                RestoreWindow(window);
        }

        public void Restore()
        {
            foreach (var window in overrides.Keys.ToArray())
                RestoreWindow(window);
        }

        void RestoreWindow(uint window)
        {
            var state = overrides[window];
            if (Owns(window))
            {
                Apply(window, icon, IconReadUnits, null, ref state.Icon);
                ApplyIdentity(window, null, null, null, state);
            }

            overrides.Remove(window);
        }

        bool Owns(uint window) =>
            TryRead(window, pid, 1, out var value) && Is32Bit(value, Cardinal) &&
            value.Data.Length == sizeof(uint) && BitConverter.ToUInt32(value.Data, 0) == processId;

        bool IsKWin()
        {
            var check = Intern("_NET_SUPPORTING_WM_CHECK");
            var name = Intern("_NET_WM_NAME");
            return TryRead(root, check, 1, out var manager) && Is32Bit(manager, Window) &&
                manager.Data.Length == sizeof(uint) &&
                TryRead(BitConverter.ToUInt32(manager.Data, 0), name, HintReadUnits, out var value) &&
                value != null && value.Type == utf8 && value.Format == 8 &&
                Encoding.UTF8.GetString(value.Data) == "KWin";
        }

        bool IsEditorWindow(uint window)
        {
            if (!TryRead(window, windowType, HintReadUnits, out var type))
                return false;

            if (type == null)
                return TryRead(window, TransientFor, 1, out var parent) && parent == null;

            if (!Is32Bit(type, Atom) || type.Data.Length == 0)
                return false;

            var primaryType = BitConverter.ToUInt32(type.Data, 0);
            return primaryType == normal || primaryType == utility;
        }

        Property FitIcons(byte[] icons)
        {
            var length = 0;
            var limit = maxRequestUnits * 4L - 28; // ChangeProperty header, including BIG-REQUESTS.
            while (length < icons.Length)
            {
                var width = BitConverter.ToInt32(icons, length);
                var height = BitConverter.ToInt32(icons, length + sizeof(uint));
                var end = length + (2 + width * height) * sizeof(uint);
                if (end > limit)
                    break;
                length = end;
            }

            if (length == 0)
                return null;
            if (length != icons.Length)
            {
                var smaller = new byte[length];
                Buffer.BlockCopy(icons, 0, smaller, 0, length);
                icons = smaller;
            }

            return new Property(Cardinal, 32, icons);
        }

        void ApplyIdentity(uint window, byte[] instance, Property gtkId, Property kdeFile, WindowState state)
        {
            // Shells such as GNOME and Plasma can match StartupWMClass before using a desktop ID.
            // Give each project its own X11 instance and preserve Unity's class for window rules.
            var changed = ApplyInstance(window, instance, ref state.Class);
            Apply(window, gtkApplicationId, HintReadUnits, gtkId, ref state.GtkId);
            changed |= Apply(window, kdeDesktopFile, HintReadUnits, kdeFile, ref state.KdeFile);
            if (changed && skipTaskbar != 0)
                RefreshTaskbar(window);
        }

        bool ApplyInstance(uint window, byte[] instance, ref Change change)
        {
            if (instance == null)
                return Apply(window, WmClass, HintReadUnits, null, ref change);

            if (!TryRead(window, WmClass, HintReadUnits, out var current) ||
                current == null || current.Type != String || current.Format != 8)
                return false;

            var separator = Array.IndexOf(current.Data, (byte)0);
            if (separator < 0 || current.Data[current.Data.Length - 1] != 0 ||
                current.Data.AsSpan(0, separator).SequenceEqual(instance))
                return false;

            var classLength = current.Data.Length - separator - 1;
            var data = new byte[instance.Length + 1 + classLength];
            Buffer.BlockCopy(instance, 0, data, 0, instance.Length);
            Buffer.BlockCopy(current.Data, separator + 1, data, instance.Length + 1, classLength);
            return Apply(window, WmClass, current, new Property(String, 8, data), ref change);
        }

        void RefreshTaskbar(uint window)
        {
            // Plasma retains groups formed before the new identity was available. A pair of
            // EWMH requests makes it reinsert the task while leaving the window mapped.
            if (!TryRead(window, windowState, HintReadUnits, out var state))
                return;
            if (state != null)
            {
                if (!Is32Bit(state, Atom))
                    return;
                for (var i = 0; i < state.Data.Length; i += sizeof(uint))
                    if (BitConverter.ToUInt32(state.Data, i) == skipTaskbar)
                        return;
            }

            var message = new ClientMessage
            {
                ResponseType = 33, Format = 32, Window = window, Type = windowState,
                Action = 1, FirstProperty = skipTaskbar, Source = 1
            };
            var add = xcb_send_event_checked(connection, 0, root, SubstructureEvents, ref message);
            message.Action = 0;
            var remove = xcb_send_event_checked(connection, 0, root, SubstructureEvents, ref message);
            free(xcb_request_check(connection, add));
            free(xcb_request_check(connection, remove));
        }

        bool Apply(uint window, uint atom, uint limit, Property desired, ref Change change)
        {
            if (atom == 0 || (desired == null && change == null) ||
                !TryRead(window, atom, limit, out var current))
                return false;

            return Apply(window, atom, current, desired, ref change);
        }

        bool Apply(uint window, uint atom, Property current, Property desired, ref Change change)
        {
            if (desired == null)
            {
                // A different writer may have updated this window since our last pass.
                var ours = Property.Same(current, change.Applied);
                if (ours && !Write(window, atom, change.Original))
                    return false;
                change = null;
                return ours;
            }

            if (Property.Same(current, desired) || !Write(window, atom, desired))
                return false;

            var original = change != null && Property.Same(current, change.Applied) ? change.Original : current;
            change = new Change(original, desired);
            return true;
        }

        bool TryRead(uint window, uint atom, uint limit, out Property property)
        {
            property = null;
            var cookie = xcb_get_property(connection, 0, window, atom, 0, 0, limit);
            var reply = xcb_get_property_reply(connection, cookie, out var error);
            try
            {
                if (error != IntPtr.Zero || reply == IntPtr.Zero)
                    return false;

                var header = Marshal.PtrToStructure<PropertyReply>(reply);
                if (header.Type == 0)
                    return true;
                if (header.BytesAfter != 0 || (header.Format != 8 && header.Format != 16 && header.Format != 32))
                    return false;

                var length = xcb_get_property_value_length(reply);
                if (length < 0 || length > limit * 4 || length % (header.Format / 8) != 0)
                    return false;
                var data = new byte[length];
                if (length != 0)
                    Marshal.Copy(xcb_get_property_value(reply), data, 0, length);
                property = new Property(header.Type, header.Format, data);
                return true;
            }
            finally
            {
                free(reply);
                free(error);
            }
        }

        bool Write(uint window, uint atom, Property property)
        {
            var cookie = property == null
                ? xcb_delete_property_checked(connection, window, atom)
                : xcb_change_property_checked(connection, 0, window, atom, property.Type, property.Format,
                    (uint)property.Data.Length / (uint)(property.Format / 8), property.Data);
            var error = xcb_request_check(connection, cookie);
            free(error);
            return error == IntPtr.Zero;
        }

        uint Intern(string name)
        {
            var cookie = xcb_intern_atom(connection, 0, (ushort)name.Length, name);
            var reply = xcb_intern_atom_reply(connection, cookie, out var error);
            try
            {
                // The atom follows the eight-byte X11 reply header.
                return reply == IntPtr.Zero ? 0 : unchecked((uint)Marshal.ReadInt32(reply, 8));
            }
            finally
            {
                free(reply);
                free(error);
            }
        }

        static bool Is32Bit(Property property, uint type) =>
            property != null && property.Type == type && property.Format == 32;

        public void Dispose() => xcb_disconnect(connection);

        sealed class WindowState
        {
            public Change Icon, Class, GtkId, KdeFile;
        }

        sealed class Change
        {
            public readonly Property Original, Applied;

            public Change(Property original, Property applied)
            {
                Original = original;
                Applied = applied;
            }
        }

        sealed class Property
        {
            public readonly uint Type;
            public readonly byte Format;
            public readonly byte[] Data;

            public Property(uint type, byte format, byte[] data)
            {
                Type = type;
                Format = format;
                Data = data;
            }

            public static bool Same(Property a, Property b) =>
                a == b || (a != null && b != null && a.Type == b.Type && a.Format == b.Format &&
                    a.Data.AsSpan().SequenceEqual(b.Data));
        }

        [StructLayout(LayoutKind.Sequential)]
        struct ScreenIterator
        {
            public IntPtr Data;
            public int Remaining, Index;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct PropertyReply
        {
            public byte ResponseType, Format;
            public ushort Sequence;
            public uint Length, Type, BytesAfter;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct ClientMessage
        {
            public byte ResponseType, Format;
            public ushort Sequence;
            public uint Window, Type, Action, FirstProperty, SecondProperty, Source, Reserved;
        }

        // XCB cookies contain a single unsigned sequence number.
        [DllImport(Xcb)] static extern IntPtr xcb_connect(string display, out int screen);
        [DllImport(Xcb)] static extern void xcb_disconnect(IntPtr connection);
        [DllImport(Xcb)] static extern int xcb_connection_has_error(IntPtr connection);
        [DllImport(Xcb)] static extern IntPtr xcb_get_setup(IntPtr connection);
        [DllImport(Xcb)] static extern ScreenIterator xcb_setup_roots_iterator(IntPtr setup);
        [DllImport(Xcb)] static extern void xcb_screen_next(ref ScreenIterator screens);
        [DllImport(Xcb)] static extern uint xcb_get_maximum_request_length(IntPtr connection);
        [DllImport(Xcb)] static extern uint xcb_intern_atom(IntPtr connection, byte onlyIfExists, ushort length, string name);
        [DllImport(Xcb)] static extern IntPtr xcb_intern_atom_reply(IntPtr connection, uint cookie, out IntPtr error);
        [DllImport(Xcb)] static extern uint xcb_get_property(IntPtr connection, byte delete, uint window, uint atom, uint type, uint offset, uint length);
        [DllImport(Xcb)] static extern IntPtr xcb_get_property_reply(IntPtr connection, uint cookie, out IntPtr error);
        [DllImport(Xcb)] static extern IntPtr xcb_get_property_value(IntPtr reply);
        [DllImport(Xcb)] static extern int xcb_get_property_value_length(IntPtr reply);
        [DllImport(Xcb)] static extern uint xcb_change_property_checked(IntPtr connection, byte mode, uint window, uint atom, uint type, byte format, uint length, byte[] data);
        [DllImport(Xcb)] static extern uint xcb_delete_property_checked(IntPtr connection, uint window, uint atom);
        [DllImport(Xcb)] static extern uint xcb_send_event_checked(IntPtr connection, byte propagate, uint destination, uint eventMask, ref ClientMessage message);
        [DllImport(Xcb)] static extern IntPtr xcb_request_check(IntPtr connection, uint cookie);
        [DllImport("libc")] static extern void free(IntPtr pointer);
    }
}
#endif
