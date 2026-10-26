using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using GodanOverlay.Panels;
using GodanOverlay.Services;
using Valve.VR;

namespace GodanOverlay;

public static class Program
{
    public const string AppKey = "vrcirodori.godan_overlay";

    [DllImport("winmm.dll")] static extern uint timeBeginPeriod(uint ms);
    [DllImport("winmm.dll")] static extern uint timeEndPeriod(uint ms);

    /// <summary>手ごとの操作状態</summary>
    sealed class HandInteraction
    {
        public Panel? Hover, Pressed, Grabbed;
        public Matrix4x4 GrabRel;
        /// <summary>もう片方の手がつかんでいるパネルを、こちらの手でもつかんで拡大縮小中</summary>
        public Panel? Scaling;
        public float ScaleStartDistance, ScaleStartWidth;
        public LaserVisual Laser = null!;
        public CursorVisual Cursor = null!;
        /// <summary>少し前までレーザーが当たっていた所（トリガーを引いた瞬間のブレを打ち消すのに使う）</summary>
        public readonly Queue<(long ms, Panel panel, Vector2 px)> History = new();
        /// <summary>押した瞬間の、実際の位置と押したことにした位置の差。押している間の位置からも引く</summary>
        public Vector2 PressOffset;
    }

    /// <summary>トリガーを引くと手が動いて狙いがずれるので、少し前に指していた位置を押した位置にする</summary>
    static Vector2 StablePressPoint(HandInteraction hi, Panel panel, Vector2 px)
    {
        int ms = App.Config.Input.PressStabilizeMs;
        if (ms <= 0) return px;
        long target = Environment.TickCount64 - ms;
        Vector2? found = null;
        foreach (var (t, p, hpx) in hi.History)
        {
            if (p != panel) { found = null; continue; } // 途中で別のパネルを指していたら使わない
            if (t <= target || found == null) found = hpx;
            if (t > target) break;
        }
        if (found is not { } f) return px;
        // 大きくずれているのはブレではなく、狙いを変えている途中（急いで隣のキーへ移った直後など）
        float meters = Vector2.Distance(f, px) * panel.WidthMeters / Math.Max(1, panel.PixW);
        return meters <= 0.03f ? f : px;
    }

    public static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Log.Info("GODAN Overlay 起動");

        if (args.Contains("--diag")) return Diag();

        int pi = Array.IndexOf(args, "--preview");
        if (pi >= 0) return Preview.Run(pi + 1 < args.Length ? args[pi + 1] : AppContext.BaseDirectory);

        bool registerMode = args.Contains("--register") || args.Contains("--unregister");

        // 二重起動すると、片方が SteamVR への接続で固まったときにもう片方も道連れになる
        using var single = new Mutex(false, @"Local\vrcirodori.godan_overlay");
        if (!registerMode)
        {
            bool first;
            // 起動し直しのときは前のプロセスが終わるのを少し待つ
            try { first = single.WaitOne(args.Contains("--restarted") ? 10000 : 0); }
            catch (AbandonedMutexException) { first = true; }
            if (!first)
            {
                Log.Info("すでに起動しています。こちらは終了します");
                return 0;
            }
        }

        if (!InitOpenVR(waitForSteamVR: !args.Contains("--no-wait"), args)) return 1;

        try
        {
            if (args.Contains("--register")) return Register(true);
            if (args.Contains("--unregister")) return Register(false);
            Run();
            return 0;
        }
        catch (Exception e)
        {
            Log.Error("致命的なエラー: " + e);
            return 1;
        }
        finally
        {
            OpenVR.Shutdown();
            Log.Info("終了");
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);

    /// <summary>--diag: vrserver のプロセスを開けるか調べる（SteamVR に繋がらない原因調査用）</summary>
    static int Diag()
    {
        foreach (var p in Process.GetProcessesByName("vrserver"))
        {
            foreach (var (name, mask) in new[] { ("SYNCHRONIZE", 0x00100000u), ("QUERY_LIMITED", 0x1000u), ("SYNC|QUERY_LIMITED", 0x00101000u), ("QUERY_INFORMATION", 0x0400u), ("ALL_ACCESS", 0x001FFFFFu) })
            {
                var h = OpenProcess(mask, false, p.Id);
                int err = Marshal.GetLastWin32Error();
                Log.Info($"vrserver {p.Id} {name}: {(h != IntPtr.Zero ? "OK" : "失敗 err=" + err)}");
                if (h != IntPtr.Zero) CloseHandle(h);
            }
        }
        try
        {
            var listener = Windows.UI.Notifications.Management.UserNotificationListener.Current;
            Log.Info("通知の権限(現在): " + listener.GetAccessStatus());
            var st = listener.RequestAccessAsync().AsTask().GetAwaiter().GetResult();
            Log.Info("通知の権限(要求後): " + st);
            if (st == Windows.UI.Notifications.Management.UserNotificationListenerAccessStatus.Allowed)
            {
                var list = listener.GetNotificationsAsync(Windows.UI.Notifications.NotificationKinds.Toast).AsTask().GetAwaiter().GetResult();
                Log.Info($"通知の件数: {list.Count}");
            }
        }
        catch (Exception e) { Log.Info("通知: 使えません " + e.GetType().Name + ": " + e.Message); }
        return 0;
    }

    /// <summary>
    /// vrserver を開く権限がないか（SteamVR が管理者権限で動いていて、こちらが一般権限のとき）。
    /// この状態で接続しようとすると OpenVR が「SteamVR がない」と判断して2つ目の SteamVR を起動してしまう。
    /// </summary>
    static bool SteamVRIsElevatedForUs()
    {
        foreach (var p in Process.GetProcessesByName("vrserver"))
        {
            var h = OpenProcess(0x0400 /* PROCESS_QUERY_INFORMATION */, false, p.Id);
            if (h == IntPtr.Zero) return true;
            CloseHandle(h);
        }
        return false;
    }

    /// <summary>SteamVR が起動しきっているか（vrserver が起動から十分たち、vrcompositor もいる）</summary>
    static bool SteamVRReady(out string why)
    {
        var servers = Process.GetProcessesByName("vrserver");
        if (servers.Length == 0) { why = "未起動"; return false; }
        if (Process.GetProcessesByName("vrcompositor").Length == 0) { why = "起動中"; return false; }
        try
        {
            // 起動した瞬間に繋ぐと VR_Init が戻ってこなくなることがあった
            if ((DateTime.Now - servers[0].StartTime).TotalSeconds < 15) { why = "起動中"; return false; }
        }
        catch { }
        why = "";
        return true;
    }

    /// <summary>
    /// VR_Init が固まったときの保険。時間内に戻ってこなければ自分を起動し直して終了する
    /// （固まった VR_Init は中断できないため）
    /// </summary>
    static void InitWithWatchdog(EVRApplicationType type, ref EVRInitError err, string[] args)
    {
        using var done = new ManualResetEventSlim();
        var watchdog = new Thread(() =>
        {
            if (done.Wait(TimeSpan.FromSeconds(20))) return;
            Log.Warn("SteamVR への接続が20秒たっても終わらないので、起動し直します");
            try
            {
                var psi = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, WorkingDirectory = AppContext.BaseDirectory };
                foreach (var a in args) psi.ArgumentList.Add(a);
                if (!args.Contains("--restarted")) psi.ArgumentList.Add("--restarted");
                Process.Start(psi);
            }
            catch (Exception e) { Log.Error("起動し直しに失敗: " + e.Message); }
            Environment.Exit(2);
        }) { IsBackground = true };
        watchdog.Start();
        try { OpenVR.Init(ref err, type); }
        finally { done.Set(); }
    }

    static bool InitOpenVR(bool waitForSteamVR, string[] args)
    {
        bool told = false;
        while (true)
        {
            // オーバーレイとして初期化すると SteamVR が勝手に起動してしまうので、
            // SteamVR が起動しきっているときだけ繋ぎに行く
            if (!SteamVRReady(out var why))
            {
                if (!waitForSteamVR)
                {
                    Log.Error("SteamVR が起動していません");
                    return false;
                }
                if (!told)
                {
                    Log.Info($"SteamVR の起動を待っています…（{why}）");
                    told = true;
                }
                Thread.Sleep(3000);
                continue;
            }

            // 権限が足りないまま接続しようとすると、SteamVR の受付窓口を壊してしまう（他のアプリも繋がらなくなる）。
            // 接続を試す前に確かめて、足りなければ何もせずに終える
            if (SteamVRIsElevatedForUs())
            {
                Log.Error("SteamVR が管理者権限で動いているため接続できません（Steam を管理者として起動していると起きます）。");
                Log.Error("run.bat から起動すれば管理者として起動します。register_steamvr.bat で自動起動を登録すれば、次からは SteamVR が起動してくれます。");
                return false;
            }

            // まず「SteamVR を絶対に起動しない」Background 型で確認する。
            // 終了しかけの SteamVR に Overlay 型で繋ぐと、SteamVR を起動し直してしまい、
            // その SteamVR はこのアプリを閉じると一緒に終了してしまう（実際に起きた）
            var err = EVRInitError.None;
            InitWithWatchdog(EVRApplicationType.VRApplication_Background, ref err, args);
            if (err != EVRInitError.None)
            {
                if (!waitForSteamVR)
                {
                    Log.Error($"SteamVR に接続できません: {err}");
                    return false;
                }
                if (!told)
                {
                    Log.Info($"SteamVR の起動を待っています… ({err})");
                    told = true;
                }
                Thread.Sleep(3000);
                continue;
            }
            OpenVR.Shutdown();

            err = EVRInitError.None;
            InitWithWatchdog(EVRApplicationType.VRApplication_Overlay, ref err, args);
            if (err == EVRInitError.None) return true;
            if (!waitForSteamVR)
            {
                Log.Error($"SteamVR に接続できません: {err}");
                return false;
            }
            if (!told)
            {
                Log.Info($"SteamVR の起動を待っています… ({err})");
                told = true;
            }
            Thread.Sleep(3000);
        }
    }

    static int Register(bool on)
    {
        var manifest = Path.Combine(AppContext.BaseDirectory, "godan_overlay.vrmanifest");
        File.WriteAllText(manifest, $$"""
        {
          "source": "builtin",
          "applications": [{
            "app_key": "{{AppKey}}",
            "launch_type": "binary",
            "binary_path_windows": "GodanOverlay.exe",
            "is_dashboard_overlay": true,
            "action_manifest_path": "input/actions.json",
            "strings": {
              "en_us": { "name": "GODAN Keyboard Overlay", "description": "GODAN keyboard and desktop panel for VRChat" },
              "ja_jp": { "name": "GODAN キーボードオーバーレイ", "description": "GODAN配列キーボードとデスクトップ表示" }
            }
          }]
        }
        """);
        if (on)
        {
            var e1 = OpenVR.Applications.AddApplicationManifest(manifest, false);
            var e2 = OpenVR.Applications.SetApplicationAutoLaunch(AppKey, true);
            Log.Info($"SteamVR に登録: manifest={e1}, autolaunch={e2}");
            return e1 == EVRApplicationError.None ? 0 : 1;
        }
        else
        {
            OpenVR.Applications.SetApplicationAutoLaunch(AppKey, false);
            var e = OpenVR.Applications.RemoveApplicationManifest(manifest);
            Log.Info($"SteamVR から登録解除: {e}");
            return 0;
        }
    }

    static void Run()
    {
        // 手で起動したときも、登録済みならこのアプリとして扱ってもらう（キー割り当てのため）
        OpenVR.Applications.IdentifyApplication((uint)Environment.ProcessId, AppKey);

        int adapter = -1;
        OpenVR.System.GetDXGIOutputInfo(ref adapter);
        Gpu.Init(adapter);

        App.Config = Config.Load();
        App.Input = new VrInput();
        App.Input.Init();
        App.Output = new Output(App.Config.Output);
        App.Converter = App.Config.Converter.Engine == "none" ? new NoConverter() : new GoogleConverter();

        App.Input.Update(); // HMDの位置を取っておく
        App.Desktop = new DesktopPanel(App.Config.Desktop);
        App.Keyboard = new KeyboardPanel(App.Config.Keyboard);
        App.Watch = new WatchPanel(App.Config.Watch);
        App.Menu = new MenuPanel();
        App.Toast = new ToastPanel();
        App.Panels.AddRange([App.Watch, App.Keyboard, App.Desktop, App.Menu]);

        App.Media.Changed += () => App.Watch.Invalidate();
        App.Media.Start();
        if (App.Config.Notifications.Enabled)
        {
            var watcher = new NotificationWatcher(App.Config.Notifications.ExcludeApps);
            watcher.Received += t => App.Toast?.Push(t);
            watcher.Start();
        }

        var hands = new HandInteraction[2];
        for (int i = 0; i < 2; i++)
            hands[i] = new HandInteraction { Laser = new LaserVisual((Hand)i, App.Config.Input.LaserWidthMeters), Cursor = new CursorVisual((Hand)i) };

        if (App.Config.Watch.ShowOnStart || App.Config.Watch.AlwaysShow) App.Watch.Show();
        Log.Info($"準備完了。時計は{(App.Config.Watch.Hand == "left" ? "左" : "右")}手。トグルボタン2回押しで表示切替");

        timeBeginPeriod(1);
        var sw = Stopwatch.StartNew();
        double last = 0, lastToggle = -10;
        var ev = new VREvent_t();
        uint evSize = (uint)Marshal.SizeOf<VREvent_t>();

        try
        {
            while (true)
            {
                // ── SteamVR からのイベント ──
                bool quit = false;
                while (OpenVR.System.PollNextEvent(ref ev, evSize))
                {
                    if ((EVREventType)ev.eventType == EVREventType.VREvent_Quit)
                    {
                        Log.Info("SteamVR が終了するので終了します");
                        OpenVR.System.AcknowledgeQuit_Exiting();
                        quit = true;
                    }
                }
                if (quit) break;

                double now = sw.Elapsed.TotalSeconds;
                double dt = Math.Min(0.1, now - last);
                last = now;

                var input = App.Input;
                input.Update();

                // ── 時計の表示切替（2回押し） ──
                if (input.ToggleDown)
                {
                    if (now - lastToggle <= App.Config.Watch.DoublePressSeconds)
                    {
                        App.Watch.ButtonToggle();
                        lastToggle = -10;
                    }
                    else lastToggle = now;
                }
                // X は右クリックのボタンも兼ねるので、押しながらトリガーを引いたら2回押しに数えない
                if (App.Config.Input.RightClickWithButton && input.Hands.Any(h => h.ClickDown) && input.Hands.Any(h => h.RightClick)) lastToggle = -10;

                var panels = App.Panels.ToArray();
                foreach (var p in panels)
                {
                    p.HudUpdate(input.HmdPose);
                    p.Update(now, dt);
                }
                App.Toast?.Update(now, dt);

                // ── レーザー操作 ──
                for (int i = 0; i < 2; i++)
                    Interact(hands[i], input.Hands[i], panels, hands, dt, input.HmdPose.Translation);

                UpdateExclusive(input, hands);

                App.RunPosted();
                RemoveClosedPanels(hands);

                foreach (var p in App.Panels) p.Flush();
                App.Toast?.Flush();
                foreach (var h in hands) { h.Laser.Flush(); h.Cursor.Flush(); }
                Gpu.Context.Flush();

                // ── 次のフレームまで待つ（だいたい表示のリフレッシュレート） ──
                var err = ETrackedPropertyError.TrackedProp_Success;
                float hz = OpenVR.System.GetFloatTrackedDeviceProperty(0, ETrackedDeviceProperty.Prop_DisplayFrequency_Float, ref err);
                if (hz < 30 || hz > 240) hz = 90;
                double wait = 1.0 / hz - (sw.Elapsed.TotalSeconds - now);
                if (wait > 0.001) Thread.Sleep(TimeSpan.FromSeconds(wait));
            }
        }
        finally
        {
            timeEndPeriod(1);
            foreach (var p in App.Panels) p.Dispose();
            App.Toast?.Dispose();
            foreach (var h in hands) { h.Laser.Dispose(); h.Cursor.Dispose(); }
            App.Config.Save();
            Gpu.Shutdown();
        }
    }

    /// <summary>閉じられたウィンドウのパネルを一覧から外して破棄する</summary>
    static void RemoveClosedPanels(HandInteraction[] hands)
    {
        foreach (var p in App.Panels.Where(p => p.IsClosed).ToList())
        {
            foreach (var h in hands)
            {
                if (h.Hover == p) h.Hover = null;
                if (h.Pressed == p) h.Pressed = null;
                if (h.Grabbed == p) h.Grabbed = null;
                if (h.Scaling == p) h.Scaling = null;
            }
            App.Panels.Remove(p);
            p.Dispose();
        }
    }

    /// <summary>
    /// VRChat から入力を取り上げるかどうか。
    /// ボタンやスティックを押している途中で取り上げると、VRChat 側は最後の値のまま止まって
    /// 「押しっぱなし」になるので、離れている（中立の）ときにだけ取り上げ始める。
    /// </summary>
    static void UpdateExclusive(VrInput input, HandInteraction[] hands)
    {
        if (!App.Config.Input.BlockGameInputWhilePointing)
        {
            input.Exclusive = input.StickExclusive = false;
            return;
        }

        bool wantButtons = hands.Any(h => h.Hover != null || h.Pressed != null || h.Grabbed != null || h.Scaling != null);
        bool buttonsNeutral = input.Hands.All(h => !h.Click && !h.Grab && !(h.RightClick && App.Config.Input.RightClickWithButton));
        input.Exclusive = wantButtons && (input.Exclusive || buttonsNeutral);

        // スティックはデスクトップのスクロールと、つかんだパネルの調整のときだけ使う
        bool wantStick = hands.Any(h => h.Grabbed != null || h.Hover is CapturePanel);
        bool stickNeutral = input.Hands.All(h => h.Stick.Length() < 0.2f);
        input.StickExclusive = wantStick && (input.StickExclusive || stickNeutral);
    }

    /// <summary>スティックをこちらで使ってよいか（VRChat と取り合っていないか）</summary>
    static bool StickIsOurs() => !App.Config.Input.BlockGameInputWhilePointing || App.Input.StickExclusive;

    static void Interact(HandInteraction hi, HandState hs, IReadOnlyList<Panel> panels, HandInteraction[] all, double dt, Vector3 eye)
    {
        var hand = hs.Hand;
        if (!hs.Tracked)
        {
            if (hi.Pressed != null) { hi.Pressed.OnRelease(hand, default); hi.Pressed = null; }
            if (hi.Grabbed != null) { hi.Grabbed.OnGrabEnd(); hi.Grabbed = null; }
            hi.Scaling = null;
            if (hi.Hover != null) { hi.Hover.OnLeave(hand); hi.Hover = null; }
            hi.Laser.Hide(); hi.Cursor.Hide();
            return;
        }

        var origin = hs.RayOrigin;
        var dir = hs.RayDir;

        // 両手でつかんで拡大縮小中（もう片方の手が動かし、こちらの手との距離で大きさを決める）
        if (hi.Scaling is { } sp)
        {
            var other = all.FirstOrDefault(o => o != hi && o.Grabbed == sp);
            var otherHand = App.Input.Hands.FirstOrDefault(h => h.Hand != hand);
            if (!hs.Grab || other == null || otherHand == null)
            {
                hi.Scaling = null;
                sp.OnGrabEnd();
            }
            else
            {
                float d = Vector3.Distance(hs.Pose.Translation, otherHand.Pose.Translation);
                if (hi.ScaleStartDistance > 0.01f)
                    sp.SetWidth(Math.Clamp(hi.ScaleStartWidth * d / hi.ScaleStartDistance, 0.05f, 5f));
                ShowPointer(hi, sp, origin, dir, eye, pressed: false);
                return;
            }
        }

        // つかんで移動中
        if (hi.Grabbed is { } g)
        {
            if (!hs.Grab)
            {
                g.OnGrabEnd();
                hi.Grabbed = null;
            }
            else
            {
                // スティック上下で遠近、左右で大きさ
                var stick = StickIsOurs() ? hs.Stick : Vector2.Zero;
                if (MathF.Abs(stick.Y) > 0.25f)
                {
                    var t = hi.GrabRel.Translation;
                    float dist = t.Length();
                    float nd = Math.Clamp(dist * (1 + stick.Y * 1.2f * (float)dt), 0.1f, 5f);
                    if (dist > 1e-4f) hi.GrabRel.Translation = t * (nd / dist);
                }
                if (MathF.Abs(stick.X) > 0.25f)
                    g.SetWidth(Math.Max(0.05f, g.WidthMeters * (1 + stick.X * 0.8f * (float)dt)));
                g.GrabMoveTo(hi.GrabRel * hs.Pose);
                ShowPointer(hi, g, origin, dir, eye, pressed: false);
                return;
            }
        }

        // 押している間は板の外にはみ出しても同じパネルが受け取る（フリック・ドラッグ用）
        if (hi.Pressed is { } pr)
        {
            pr.Project(origin, dir, out var px, out _, out _);
            px -= hi.PressOffset;
            if (hs.Click)
            {
                pr.OnDrag(hand, px);
                ShowPointer(hi, pr, origin, dir, eye, pressed: true);
                return;
            }
            pr.OnRelease(hand, px);
            hi.Pressed = null;
        }

        // 一番手前に当たっているパネル
        Panel? best = null;
        Vector2 bestPx = default;
        float bestDist = float.MaxValue;
        foreach (var p in panels)
        {
            if (p is WatchPanel w && w.AttachedHand == hand) continue; // 時計を付けた手からは押さない
            if (p.HitTest(origin, dir, out var px, out var d, out _) && d < bestDist)
            {
                best = p; bestPx = px; bestDist = d;
            }
        }

        if (best != hi.Hover)
        {
            hi.Hover?.OnLeave(hand);
            hi.Hover = best;
        }
        if (best == null)
        {
            hi.History.Clear();
            hi.Laser.Hide();
            hi.Cursor.Hide();
            return;
        }

        long nowMs = Environment.TickCount64;
        hi.History.Enqueue((nowMs, best, bestPx));
        while (hi.History.Count > 0 && hi.History.Peek().ms < nowMs - 300) hi.History.Dequeue();

        best.OnHover(hand, bestPx);
        if (hs.ClickDown)
        {
            var pressPx = StablePressPoint(hi, best, bestPx);
            hi.PressOffset = bestPx - pressPx;
            hi.Pressed = best;
            best.OnPress(hand, pressPx);
        }
        else if (hs.GrabDown && best.Grabbable && all.Any(o => o != hi && o.Grabbed == best))
        {
            // もう片方の手がつかんでいるパネルをつかむと、両手で拡大縮小
            var otherHand = App.Input.Hands.First(h => h.Hand != hand);
            hi.Scaling = best;
            hi.ScaleStartDistance = Vector3.Distance(hs.Pose.Translation, otherHand.Pose.Translation);
            hi.ScaleStartWidth = best.WidthMeters;
            App.Input.Haptic(hand, 0.02f, 0.4f);
        }
        else if (hs.GrabDown && best.Grabbable)
        {
            hi.Grabbed = best;
            hi.GrabRel = best.World * hs.Pose.Inverse();
            App.Input.Haptic(hand, 0.02f, 0.4f);
        }
        else if (hs.Stick != Vector2.Zero && StickIsOurs())
        {
            best.OnScroll(hand, hs.Stick, dt);
        }
        ShowPointer(hi, best, origin, dir, eye, pressed: hi.Pressed != null);
    }

    static void ShowPointer(HandInteraction hi, Panel target, Vector3 origin, Vector3 dir, Vector3 eye, bool pressed)
    {
        if (!target.Project(origin, dir, out _, out var dist, out var hit, out var normal))
        {
            hi.Laser.Hide();
            hi.Cursor.Hide();
            return;
        }
        hi.Laser.Aim(origin, dir, dist, eye);
        hi.Laser.Show();
        hi.Cursor.Place(hit, normal, target.World, origin, pressed);
        hi.Cursor.Show();
    }
}
