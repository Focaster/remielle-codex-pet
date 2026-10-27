// Remielle desktop pet (WPF / .NET Framework 4.x)
// Build:
//   csc /codepage:65001 /target:winexe /out:remielle-pet.exe ^
//       /r:<fw>\WPF\PresentationFramework.dll /r:<fw>\WPF\PresentationCore.dll ^
//       /r:<fw>\WPF\WindowsBase.dll /r:<fw>\System.Xaml.dll Pet.cs
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WinForms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace Remielle
{
    internal sealed class Anim
    {
        public double Fps = 8;
        public bool Loop = true;
        public List<BitmapImage> Frames = new List<BitmapImage>();
    }

    internal static class Program
    {
        private static Mutex _single;

        [STAThread]
        private static void Main(string[] args)
        {
            bool createdNew;
            _single = new Mutex(true, "Local\\RemielleDesktopPet", out createdNew);
            if (!createdNew) return;

            var linked = Array.IndexOf(args, "--linked") >= 0;
            var app = new Application();
            app.ShutdownMode = ShutdownMode.OnMainWindowClose;
            app.Run(new PetWindow(linked));
        }
    }

    internal sealed class PetWindow : Window
    {
        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X; public int Y; }
        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT p);
        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TRANSPARENT = 0x00000020;
        private const int WS_EX_LAYERED = 0x00080000;

        private readonly Image _img = new Image();
        private readonly Dictionary<string, Anim> _anims = new Dictionary<string, Anim>();
        private readonly DispatcherTimer _tick = new DispatcherTimer();
        private readonly DispatcherTimer _poll = new DispatcherTimer();
        private readonly DispatcherTimer _idleBack = new DispatcherTimer();
        private readonly string _root;
        private readonly string _stateFile;

        private string _base = "idle";
        private string _cur = "idle";
        private int _frame;
        private double _acc;
        private bool _oneShot;
        private DateTime _lastTick = DateTime.Now;
        private DateTime? _pendingIdle;

        private Point? _downPoint;
        private bool _dragged;
        private DateTime _hoverAt = DateTime.MinValue;

        private string _port;
        private int _lastSeq = -1;
        private string _lastState = "";
        private int _polling;

        private bool _dragging;
        private POINT _dragLastCursor;
        private string _runDir = "runR";
        private int _lastFailSeq = -1;
        private DateTime _failUntil = DateTime.MinValue;

        private Window _bubbleWin;
        private Border _bubbleBorder;
        private TextBlock _bubbleText;
        private DispatcherTimer _bubbleTimer;
        private DateTime _lastClickAt = DateTime.MinValue;

        private sealed class Evt { public string Anim; public int Priority; public int CooldownMs; public int RepeatMs; }
        private readonly Dictionary<string, Evt> _events = new Dictionary<string, Evt>();
        private readonly Dictionary<string, DateTime> _lastFired = new Dictionary<string, DateTime>();
        private int _oneShotPriority;
        private string _repeatAnim;
        private DateTime _repeatUntil = DateTime.MinValue;

        private WinForms.NotifyIcon _tray;
        private bool _clickThrough;
        private readonly bool _linked;
        private int _missCount;
        private string _hotkeyLabel = "Ctrl+Alt+R";

        private const int WM_HOTKEY = 0x0312;
        private const uint MOD_ALT = 0x0001;
        private const uint MOD_CONTROL = 0x0002;
        private const uint MOD_SHIFT = 0x0004;
        private const uint MOD_NOREPEAT = 0x4000;
        private const uint VK_R = 0x52;

        [DllImport("user32.dll")]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
        private readonly DispatcherTimer _gaze = new DispatcherTimer();
        private bool _lookAtCursor = true;
        private bool _gazing;
        private int _gazeIndex = -1;
        private DateTime _lastCursorMove = DateTime.MinValue;
        private Point _lastCursor;

        public PetWindow(bool linked)
        {
            _linked = linked;
            _root = AppDomain.CurrentDomain.BaseDirectory;
            var stateDir = _root;
            _stateFile = Path.Combine(stateDir, "pet-state.txt");
            Directory.CreateDirectory(stateDir);

            Title = "\u5c0f\u857e\u7c73";
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Topmost = true;
            ShowInTaskbar = false;
            ShowActivated = false;
            ResizeMode = ResizeMode.NoResize;

            _img.Stretch = Stretch.Fill;
            RenderOptions.SetBitmapScalingMode(_img, BitmapScalingMode.HighQuality);
            Content = _img;

            DefineAnims();
            LoadAllFrames();
            if (_anims["idle"].Frames.Count == 0)
            {
                MessageBox.Show("No frames found: " + Path.Combine(_root, "frames"), "Remielle");
                Application.Current.Shutdown();
                return;
            }

            int width = 144, left = -1, top = -1;
            LoadState(ref left, ref top, ref width);
            ApplySize(width);
            PlaceAt(left, top);

            _img.Source = _anims["idle"].Frames[0];
            HookEvents();
            BuildMenu();

            _tick.Interval = TimeSpan.FromMilliseconds(33);
            _tick.Tick += OnTick;
            _tick.Start();

            _idleBack.Interval = TimeSpan.FromMilliseconds(250);
            _idleBack.Tick += (s, e) =>
            {
                if (_pendingIdle.HasValue && DateTime.Now >= _pendingIdle.Value)
                {
                    _pendingIdle = null;
                    SetBase("idle");
                }
            };
            _idleBack.Start();

            _poll.Interval = TimeSpan.FromMilliseconds(1200);
            _poll.Tick += (s, e) => PollTick();
            _poll.Start();
            var hb = new DispatcherTimer();
            hb.Interval = TimeSpan.FromSeconds(30);
            hb.Tick += (s, e) => Log("heartbeat state=" + _lastState + " cur=" + _cur + " base=" + _base
                + " shot=" + _oneShot + " gaze=" + _gazing + " pend=" + (_pendingIdle.HasValue ? "yes" : "no") + " port=" + (_port ?? "-"));
            hb.Start();
            _gaze.Interval = TimeSpan.FromMilliseconds(80);
            _gaze.Tick += (s, e) => { UpdateDragDirection(); UpdateGaze(); };
            _gaze.Start();

            BuildBubble();
            BuildTray();
            SourceInitialized += (s, e) => HookHotkey();

            // 原版 animation-triggers.json 的优先级与冷却
            _events["app_start"] = new Evt { Anim = "wave", Priority = 80, CooldownMs = 30000 };
            _events["pointer_enter"] = new Evt { Anim = "wave", Priority = 80, CooldownMs = 10000, RepeatMs = 2100 };
            _events["pet_click"] = new Evt { Anim = "wave", Priority = 80, CooldownMs = 2500 };
            _events["task_complete"] = new Evt { Anim = "smug", Priority = 100, CooldownMs = 3000 };
            _events["double_click"] = new Evt { Anim = "smug", Priority = 100, CooldownMs = 3000 };
            _events["failure"] = new Evt { Anim = "failed", Priority = 110, CooldownMs = 3000 };

            Closed += (s, e) =>
            {
                SaveState();
                if (_bubbleWin != null) _bubbleWin.Close();
                try { UnregisterHotKey(new System.Windows.Interop.WindowInteropHelper(this).Handle, 1); } catch { }
                if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
            };
            Loaded += (s, e) =>
            {
                Topmost = true;
                Log("started, width=" + (int)Width + (_linked ? " (linked)" : " (standalone)"));
                if (_bubbleWin != null) { _bubbleWin.Owner = this; _bubbleWin.Show(); _bubbleWin.Hide(); }
                Trigger("app_start");
                Say("\u5c0f\u857e\u7c73\u6765\u5566\uff5e");
            };
        }

        // ---- logging ---------------------------------------------------
        private static void Log(string msg)
        {
            var line = DateTime.Now.ToString("HH:mm:ss") + "  " + msg + Environment.NewLine;
            var candidates = new string[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "remielle-desktop", "pet.log"),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "pet.log"),
            };
            foreach (var file in candidates)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(file));
                    var fi = new FileInfo(file);
                    if (fi.Exists && fi.Length > 512 * 1024) fi.Delete();
                    File.AppendAllText(file, line);
                    return;
                }
                catch { }
            }
        }

        // ---- frames ----------------------------------------------------
        private void DefineAnims()
        {
            Add("idle", 8, true);
            Add("runR", 12, true);
            Add("runL", 12, true);
            Add("wave", 5.714, false);
            Add("smug", 5.952, false);
            Add("failed", 10, false);
            Add("waiting", 5, true);
            Add("working", 11, true);
            Add("review", 8, true);
            Add("gaze", 1, false);
        }

        private void Add(string name, double fps, bool loop)
        {
            _anims[name] = new Anim { Fps = fps, Loop = loop };
        }

        private void LoadAllFrames()
        {
            foreach (var key in new List<string>(_anims.Keys))
            {
                _anims[key].Frames = LoadFrames(Path.Combine(_root, "frames", key));
            }
        }

        private static List<BitmapImage> LoadFrames(string dir)
        {
            var list = new List<BitmapImage>();
            if (!Directory.Exists(dir)) return list;
            var files = Directory.GetFiles(dir, "*.png");
            Array.Sort(files, delegate(string a, string b)
            {
                int ia = 0, ib = 0;
                int.TryParse(Path.GetFileNameWithoutExtension(a), out ia);
                int.TryParse(Path.GetFileNameWithoutExtension(b), out ib);
                return ia.CompareTo(ib);
            });
            foreach (var f in files)
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.UriSource = new Uri(f);
                bmp.EndInit();
                bmp.Freeze();
                list.Add(bmp);
            }
            return list;
        }

        // ---- size / position -------------------------------------------
        private void ApplySize(int width)
        {
            Width = width;
            Height = width * 208.0 / 192.0;
            _img.Width = Width;
            _img.Height = Height;
        }

        private void PlaceAt(int left, int top)
        {
            var wa = SystemParameters.WorkArea;
            if (left < 0 || top < 0)
            {
                Left = wa.Right - Width - 40;
                Top = wa.Bottom - Height - 60;
            }
            else
            {
                Left = left;
                Top = top;
            }
            if (Left < wa.Left - 400 || Left > wa.Right + 400 || Top < wa.Top - 400 || Top > wa.Bottom + 400)
            {
                Left = wa.Right - Width - 40;
                Top = wa.Bottom - Height - 60;
            }
        }

        private void LoadState(ref int left, ref int top, ref int width)
        {
            try
            {
                if (!File.Exists(_stateFile)) return;
                var parts = File.ReadAllText(_stateFile).Split('|');
                if (parts.Length < 3) return;
                int l, t, w;
                if (int.TryParse(parts[0], out l)) left = l;
                if (int.TryParse(parts[1], out t)) top = t;
                if (int.TryParse(parts[2], out w) && w >= 64 && w <= 256) width = w;
            }
            catch { }
        }

        private void SaveState()
        {
            try
            {
                File.WriteAllText(_stateFile, string.Format(CultureInfo.InvariantCulture, "{0}|{1}|{2}",
                    (int)Math.Round(Left), (int)Math.Round(Top), (int)Math.Round(Width)));
            }
            catch { }
        }

        // ---- animation --------------------------------------------------
        private void ShowFrame()
        {
            var a = _anims[_cur];
            if (a.Frames.Count == 0) return;
            _img.Source = a.Frames[Math.Min(_frame, a.Frames.Count - 1)];
        }

        private void SetBase(string name)
        {
            if (!_anims.ContainsKey(name)) name = "idle";
            _base = name;
            if (_oneShot) return;
            if (_cur != name)
            {
                _cur = name;
                _frame = 0;
                _acc = 0;
            }
            ShowFrame();
        }

        private bool Trigger(string evtName)
        {
            Evt ev;
            if (!_events.TryGetValue(evtName, out ev)) return false;
            DateTime last;
            if (_lastFired.TryGetValue(evtName, out last) &&
                (DateTime.Now - last).TotalMilliseconds < ev.CooldownMs)
            {
                Log("skip " + evtName + " (cooldown)");
                return false;
            }
            if (_oneShot && ev.Priority <= _oneShotPriority)
            {
                Log("skip " + evtName + " (priority " + ev.Priority + " <= " + _oneShotPriority + ")");
                return false;
            }
            _lastFired[evtName] = DateTime.Now;
            _oneShotPriority = ev.Priority;
            Log("trigger " + evtName + " -> " + ev.Anim + " p" + ev.Priority);
            PlayOnce(ev.Anim);
            if (ev.RepeatMs > 0)
            {
                _repeatAnim = ev.Anim;
                _repeatUntil = DateTime.Now.AddMilliseconds(ev.RepeatMs);
            }
            else
            {
                _repeatAnim = null;
                _repeatUntil = DateTime.MinValue;
            }
            return true;
        }

        private void PlayOnce(string name)
        {
            Anim a;
            if (!_anims.TryGetValue(name, out a) || a.Loop) return;
            if (_repeatAnim != null && _repeatAnim != name)
            {
                _repeatAnim = null;
                _repeatUntil = DateTime.MinValue;
            }
            _oneShot = true;
            _cur = name;
            _frame = 0;
            _acc = 0;
            ShowFrame();
        }

        private void OnTick(object sender, EventArgs e)
        {
            if (_gazing)
            {
                _lastTick = DateTime.Now;
                return;
            }            var now = DateTime.Now;
            var dt = (now - _lastTick).TotalMilliseconds;
            _lastTick = now;

            var a = _anims[_cur];
            if (a.Frames.Count == 0) return;

            _acc += dt;
            var step = 1000.0 / Math.Max(1.0, a.Fps);
            var guard = 0;
            while (_acc >= step && guard++ < 120)
            {
                _acc -= step;
                _frame++;
                if (_frame < a.Frames.Count) continue;
                if (a.Loop)
                {
                    _frame = 0;
                }
                else
                {
                    _oneShot = false;
                    _oneShotPriority = 0;
                    if (DateTime.Now < _failUntil)
                    {
                        PlayOnce("failed");
                        return;
                    }
                    if (_repeatAnim != null && DateTime.Now < _repeatUntil)
                    {
                        Log("repeat " + _repeatAnim);
                        PlayOnce(_repeatAnim);
                        return;
                    }
                    _cur = _base;
                    _frame = 0;
                    _acc = 0;
                    a = _anims[_cur];
                }
            }
            ShowFrame();
        }

        // ---- DSH state ---------------------------------------------------
        private void PollTick()
        {
            if (Interlocked.CompareExchange(ref _polling, 1, 0) != 0) return;
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    if (_port == null)
                    {
                        _port = DiscoverPort();
                        Log(_port == null ? "discover: no dsh port" : "discover: port=" + _port);
                    }
                    if (_port == null)
                    {
                        NoteMiss();
                        if (_missCount >= 3) Dispatcher.BeginInvoke(new Action(delegate { SetBase("idle"); }));
                        return;
                    }
                    var txt = DownloadState(_port);
                    if (txt == null)
                    {
                        Log("state request failed, port=" + _port);
                        _port = null;
                        NoteMiss();
                        if (_missCount >= 3) Dispatcher.BeginInvoke(new Action(delegate { SetBase("idle"); }));
                        return;
                    }
                    _missCount = 0;
                    var captured = txt;
                    Dispatcher.BeginInvoke(new Action(delegate { ApplyState(captured); }));
                }
                catch { }
                finally
                {
                    Interlocked.Exchange(ref _polling, 0);
                }
            });
        }

        private static string DownloadState(string port)
        {
            try
            {
                var req = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:" + port + "/dsh-remielle/state.json");
                req.Timeout = 500;
                req.ReadWriteTimeout = 500;
                req.Proxy = null;
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var sr = new StreamReader(resp.GetResponseStream()))
                {
                    return sr.ReadToEnd();
                }
            }
            catch { return null; }
        }

        private static string DiscoverPort()
        {
            try
            {
                var props = System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties();
                foreach (var ep in props.GetActiveTcpListeners())
                {
                    var addr = ep.Address.ToString();
                    if (addr != "127.0.0.1" && addr != "0.0.0.0" && addr != "::" && addr != "::1") continue;
                    var txt = DownloadState(ep.Port.ToString());
                    if (txt != null && txt.IndexOf("\"ok\":true", StringComparison.Ordinal) >= 0)
                        return ep.Port.ToString();
                }
            }
            catch { }
            return null;
        }

        private void ApplyState(string json)
        {
            var ms = Regex.Match(json, "\"state\"\\s*:\\s*\"([a-zA-Z]+)\"");
            var md = Regex.Match(json, "\"doneSeq\"\\s*:\\s*(\\d+)");
            if (md.Success)
            {
                var seq = int.Parse(md.Groups[1].Value, CultureInfo.InvariantCulture);
                if (_lastSeq >= 0 && seq > _lastSeq)
                {
                    Trigger("task_complete");
                    Say("\u5b8c\u6210\u5566\uff01");
                    Log("turn end (seq=" + seq + ")");
                }
                _lastSeq = seq;
            }
            var mf = Regex.Match(json, "\"failSeq\"\\s*:\\s*(\\d+)");
            if (mf.Success)
            {
                var fseq = int.Parse(mf.Groups[1].Value, CultureInfo.InvariantCulture);
                if (_lastFailSeq >= 0 && fseq > _lastFailSeq)
                {
                    _failUntil = DateTime.Now.AddSeconds(3.2);
                    Trigger("failure");
                    Say("\u51fa\u9519\u4e86\u2026");
                    Log("turn failed (seq=" + fseq + ") -> failed anim 3s");
                }
                _lastFailSeq = fseq;
            }
            var state = ms.Success ? ms.Groups[1].Value : "idle";
            var next = state == "waiting" ? "waiting" : state == "working" ? "working" : "idle";
            if (GazeDebug() && File.Exists(Path.Combine(_root, "force-idle.txt"))) next = "idle";
            if (next != _lastState)
            {
                _lastState = next;
                Log("state=" + next + " seq=" + _lastSeq);
                if (next == "waiting") Say("\u5728\u7b49\u4f60\u54e6\u2026");
            }
            // 每轮都对齐一次，避免 base 因瞬时故障掉到 idle 后卡死
            SetBase(next);
        }

        // ---- drag direction --------------------------------------------
        private void UpdateDragDirection()
        {
            if (!_dragging) return;
            POINT p;
            if (!GetCursorPos(out p)) return;
            var dx = p.X - _dragLastCursor.X;
            if (Math.Abs(dx) < 2) return;
            _dragLastCursor = p;
            var dir = dx < 0 ? "runL" : "runR";
            _cur = dir;
            _runDir = dir;
            _frame = 0;
            _acc = 0;
            ShowFrame();
            if (GazeDebug()) Log("drag=" + dir);
        }

        // ---- tray + hotkey -----------------------------------------------
        private void BuildTray()
        {
            try
            {
                _tray = new WinForms.NotifyIcon();
                _tray.Icon = Drawing.SystemIcons.Application;
                _tray.Text = "\u5c0f\u857e\u7c73";
                _tray.Visible = true;

                var menu = new WinForms.ContextMenuStrip();
                menu.Items.Add("\u663e\u793a / \u9690\u85cf", null, delegate { ToggleVisible(); });
                menu.Items.Add("\u9f20\u6807\u7a7f\u900f", null, delegate { SetClickThrough(!_clickThrough); });
                menu.Items.Add("\u5f52\u4f4d", null, delegate
                {
                    var wa = SystemParameters.WorkArea;
                    Left = wa.Right - Width - 40;
                    Top = wa.Bottom - Height - 60;
                    SaveState();
                });
                menu.Items.Add(new WinForms.ToolStripSeparator());
                menu.Items.Add("\u9000\u51fa", null, delegate { SaveState(); Close(); });
                _tray.ContextMenuStrip = menu;
                _tray.DoubleClick += delegate { ToggleVisible(); };
            }
            catch { }
        }

        private void ToggleVisible()
        {
            try
            {
                if (IsVisible) Hide();
                else Show();
            }
            catch { }
        }

        private void HookHotkey()
        {
            try
            {
                var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                if (hwnd == IntPtr.Zero) return;
                var candidates = new uint[][]
                {
                    new uint[] { MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, VK_R },
                    new uint[] { MOD_CONTROL | MOD_ALT | MOD_SHIFT | MOD_NOREPEAT, VK_R },
                    new uint[] { MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, 0x78 },
                    new uint[] { MOD_CONTROL | MOD_SHIFT | MOD_NOREPEAT, VK_R },
                };
                var labels = new string[] { "Ctrl+Alt+R", "Ctrl+Alt+Shift+R", "Ctrl+Alt+F9", "Ctrl+Shift+R" };
                for (var i = 0; i < candidates.Length; i++)
                {
                    if (RegisterHotKey(hwnd, 1, candidates[i][0], candidates[i][1]))
                    {
                        _hotkeyLabel = labels[i];
                        Log("hotkey ok: " + _hotkeyLabel);
                        break;
                    }
                }
                var src = System.Windows.Interop.HwndSource.FromHwnd(hwnd);
                if (src != null) src.AddHook(OnWndProc);
            }
            catch { }
        }

        private IntPtr OnWndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_HOTKEY && wParam.ToInt32() == 1)
            {
                SetClickThrough(!_clickThrough);
                handled = true;
            }
            return IntPtr.Zero;
        }

        // ---- bubble ------------------------------------------------------
        private void BuildBubble()
        {
            _bubbleWin = new Window
            {
                Title = "\u6c14\u6ce1",
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = Brushes.Transparent,
                Topmost = true,
                ShowInTaskbar = false,
                ShowActivated = false,
                Focusable = false,
                IsHitTestVisible = false,
                ResizeMode = ResizeMode.NoResize,
                SizeToContent = SizeToContent.Manual,
            };
            _bubbleBorder = new Border
            {
                CornerRadius = new CornerRadius(10),
                Background = new SolidColorBrush(Color.FromArgb(235, 28, 28, 34)),
                Padding = new Thickness(10, 6, 10, 6),
                Child = null,
            };
            _bubbleText = new TextBlock
            {
                Foreground = Brushes.White,
                FontSize = 12,
                FontFamily = new FontFamily("Microsoft YaHei UI"),
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                MaxWidth = 220,
            };
            _bubbleBorder.Child = _bubbleText;
            _bubbleWin.Content = _bubbleBorder;
            _bubbleWin.Loaded += (s, e) => SetBubbleClickThrough();

            _bubbleTimer = new DispatcherTimer();
            _bubbleTimer.Interval = TimeSpan.FromSeconds(2.6);
            _bubbleTimer.Tick += (s, e) => { _bubbleTimer.Stop(); HideBubble(); };
        }

        private void SetBubbleClickThrough()
        {
            if (_bubbleWin == null) return;
            var hwnd = new System.Windows.Interop.WindowInteropHelper(_bubbleWin).Handle;
            if (hwnd == IntPtr.Zero) return;
            var style = GetWindowLong(hwnd, GWL_EXSTYLE);
            style |= WS_EX_TRANSPARENT | WS_EX_LAYERED;
            SetWindowLong(hwnd, GWL_EXSTYLE, style);
        }

        private static readonly string[] ClickLines = new string[]
        {
            "\u4eca\u5929\u4e5f\u8981\u52a0\u6cb9\u54e6",
            "\u8981\u8bb0\u5f97\u4f11\u606f\u5440",
            "\u6709\u4ec0\u4e48\u8981\u5e2e\u5fd9\u7684\u5417\uff1f",
            "\u8fd9\u9898\u6211\u4f1a\uff01",
            "\u8bf6\u563f\uff5e",
        };

        private static string PickLine()
        {
            return ClickLines[new Random().Next(ClickLines.Length)];
        }

        private void Say(string text)
        {
            try
            {
                if (_bubbleWin == null) return;
                _bubbleText.Text = text;
                _bubbleBorder.Measure(new Size(240, double.PositiveInfinity));
                var size = _bubbleBorder.DesiredSize;
                var w = Math.Max(64, Math.Min(236, size.Width + 4));
                var h = Math.Max(30, size.Height + 4);
                _bubbleWin.Width = w;
                _bubbleWin.Height = h;

                var wa = SystemParameters.WorkArea;
                var left = Left + (Width - w) / 2.0;
                var top = Top - h - 6;
                if (top < wa.Top + 4) top = Top + Height + 6;
                _bubbleWin.Left = Math.Max(wa.Left + 4, Math.Min(left, wa.Right - w - 4));
                _bubbleWin.Top = top;

                if (!_bubbleWin.IsVisible) _bubbleWin.Show();
                _bubbleTimer.Stop();
                _bubbleTimer.Start();
                Log("say: " + text);
            }
            catch { }
        }

        private void HideBubble()
        {
            try
            {
                if (_bubbleWin != null && _bubbleWin.IsVisible) _bubbleWin.Hide();
            }
            catch { }
        }

        // ---- gaze -------------------------------------------------------
        private void UpdateGaze()
        {
            if (!_lookAtCursor) { StopGaze(); return; }
            POINT p;
            if (!GetCursorPos(out p)) { StopGaze(); return; }
            var cur = new Point(p.X, p.Y);
            if ((cur - _lastCursor).Length > 2)
            {
                _lastCursor = cur;
                _lastCursorMove = DateTime.Now;
            }

            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            RECT r;
            if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out r)) { StopGaze(); return; }

            var cx = (r.Left + r.Right) / 2.0;
            var cy = r.Top + (r.Bottom - r.Top) * 0.42;
            double dx = p.X - cx;
            double dy = p.Y - cy;
            var dist = Math.Sqrt(dx * dx + dy * dy);

            var inside = _gazing ? dist <= 620 : dist <= 460;
            if (inside && (DateTime.Now - _lastCursorMove).TotalSeconds > 2.5) inside = false;
            if (inside && (_oneShot || _base != "idle" || _dragged)) inside = false;
            if (!inside) { StopGaze(); return; }

            var a = Math.Atan2(-dy, dx) * 180.0 / Math.PI;
            var sprite = (90.0 - a) % 360.0;
            if (sprite < 0) sprite += 360.0;
            var idx = ((int)Math.Round(sprite / 22.5)) % 16;

            _gazing = true;
            if (idx == _gazeIndex) return;
            _gazeIndex = idx;
            var list = _anims["gaze"].Frames;
            if (list.Count > idx) _img.Source = list[idx];
            if (GazeDebug())
                Log("gaze=" + idx + " dir=" + (int)Math.Round(a));
        }

        private bool GazeDebug()
        {
            return File.Exists(Path.Combine(_root, "gaze-debug.txt"));
        }
        private void StopGaze()
        {
            if (!_gazing) return;
            _gazing = false;
            _gazeIndex = -1;
            ShowFrame();
            if (GazeDebug()) Log("gaze=off");
        }
        private void NoteMiss()
        {
            _missCount++;
            if (_linked && _missCount >= 6)
            {
                Log("linked mode: dsh unavailable, exiting");
                Dispatcher.BeginInvoke(new Action(delegate { SaveState(); Close(); }));
            }
        }

        // ---- interaction --------------------------------------------------
        private void HookEvents()
        {
            MouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e)
            {
                if (e.ChangedButton != MouseButton.Left) return;
                _downPoint = e.GetPosition(this);
                _dragged = false;
            };

            MouseMove += delegate(object s, MouseEventArgs e)
            {
                if (_downPoint == null) return;
                if (e.LeftButton != MouseButtonState.Pressed) return;
                var p = e.GetPosition(this);
                if (Math.Abs(p.X - _downPoint.Value.X) + Math.Abs(p.Y - _downPoint.Value.Y) <= 4) return;
                _downPoint = null;
                _dragged = true;
                _dragging = true;
                HideBubble();
                _oneShot = true;
                _runDir = "runR";
                _cur = "runR";
                _frame = 0;
                _acc = 0;
                ShowFrame();
                GetCursorPos(out _dragLastCursor);
                try { DragMove(); } catch { }
                _dragging = false;
                _oneShot = false;
                _pendingIdle = DateTime.Now.AddMilliseconds(450);
                SaveState();
            };

            MouseLeftButtonUp += delegate(object s, MouseButtonEventArgs e)
            {
                if (_downPoint == null) return;
                _downPoint = null;
                if (_dragged) { _dragged = false; return; }
                var now = DateTime.Now;
                if ((now - _lastClickAt).TotalMilliseconds < 420)
                {
                    _lastClickAt = DateTime.MinValue;
                    Trigger("double_click");
                    Say("\u8bf6\u563f\uff5e");
                    return;
                }
                _lastClickAt = now;
                Trigger("pet_click");
                Say(PickLine());
            };

            MouseEnter += delegate
            {
                if ((DateTime.Now - _hoverAt).TotalSeconds < 8) return;
                _hoverAt = DateTime.Now;
                Trigger("pointer_enter");
            };
        }

        private void BuildMenu()
        {
            var menu = new ContextMenu();

            var size = new MenuItem { Header = "Size" };
            var options = new[] { new object[] { "S 112", 112 }, new object[] { "M 144", 144 }, new object[] { "L 180", 180 } };
            foreach (var pair in options)
            {
                var mi = new MenuItem { Header = pair[0], Tag = pair[1] };
                mi.Click += delegate(object s, RoutedEventArgs e)
                {
                    var target = (int)((MenuItem)s).Tag;
                    ApplySize(target);
                    SaveState();
                };
                size.Items.Add(mi);
            }
            menu.Items.Add(size);

            var pierce = new MenuItem { Header = "Click-through", IsCheckable = true };
            pierce.Click += delegate(object s, RoutedEventArgs e) { SetClickThrough(((MenuItem)s).IsChecked); };
            menu.Items.Add(pierce);

            var look = new MenuItem { Header = "Look at cursor", IsCheckable = true, IsChecked = true };
            look.Click += delegate(object s, RoutedEventArgs e)
            {
                _lookAtCursor = ((MenuItem)s).IsChecked;
                if (!_lookAtCursor) StopGaze();
            };
            menu.Items.Add(look);
            var reset = new MenuItem { Header = "Reset position" };
            reset.Click += delegate
            {
                var wa = SystemParameters.WorkArea;
                Left = wa.Right - Width - 40;
                Top = wa.Bottom - Height - 60;
                SaveState();
            };
            menu.Items.Add(reset);

            menu.Items.Add(new Separator());

            var quit = new MenuItem { Header = "Quit" };
            quit.Click += delegate { SaveState(); Close(); };
            menu.Items.Add(quit);

            ContextMenu = menu;
        }

        private void SetClickThrough(bool on)
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            var style = GetWindowLong(hwnd, GWL_EXSTYLE);
            if (on) style |= WS_EX_TRANSPARENT | WS_EX_LAYERED;
            else style &= ~WS_EX_TRANSPARENT;
            SetWindowLong(hwnd, GWL_EXSTYLE, style);
            _clickThrough = on;
            Log("clickthrough=" + on);
            if (on) Say("\u7a7f\u900f\u5df2\u5f00\uff08" + _hotkeyLabel + " \u53ef\u5173\uff09");
        }
    }
}
