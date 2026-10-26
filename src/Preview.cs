using System.Diagnostics;
using GodanOverlay.Panels;
using GodanOverlay.Services;

namespace GodanOverlay;

/// <summary>
/// --preview: SteamVR なしで各パネルを描いて PNG に書き出す（見た目と入力ロジックの確認用）
/// </summary>
public static class Preview
{
    public static int Run(string outDir)
    {
        Directory.CreateDirectory(outDir);
        Panel.Headless = true;
        Gpu.Init(-1);
        App.Config = new Config();
        App.Config.Keyboard.Style = "godan"; // 下の確認はGODAN配列で打つ。フリックは最後に切り替えて確認する
        App.Input = new VrInput();
        App.Output = new Output(App.Config.Output);
        App.Converter = new GoogleConverter();
        App.Desktop = new DesktopPanel(App.Config.Desktop);
        App.Keyboard = new KeyboardPanel(App.Config.Keyboard);
        App.Watch = new WatchPanel(App.Config.Watch);
        App.Menu = new MenuPanel();
        App.Toast = new ToastPanel();
        App.Panels.AddRange([App.Watch, App.Keyboard, App.Desktop, App.Menu]);
        App.Media.Start();
        CurvatureTest();
        var sw = Stopwatch.StartNew();

        void Pump(double seconds)
        {
            var end = sw.Elapsed.TotalSeconds + seconds;
            while (sw.Elapsed.TotalSeconds < end)
            {
                double now = sw.Elapsed.TotalSeconds;
                foreach (var p in App.Panels.ToArray()) p.Update(now, 0.016);
                App.RunPosted();
                foreach (var p in App.Panels) p.Flush();
                App.Toast.Flush();
                Gpu.Context.Flush();
                Thread.Sleep(16);
            }
        }

        var kb = App.Keyboard;
        kb.Show();

        // 「こんにちは、」→ 自動候補
        foreach (var k in new[] { "k", "o", "n", "n", "i", "t", "i", "h", "a" }) kb.DebugTap(k);
        kb.DebugTap("r", FlickDir.Down);
        Log.Info("入力結果: " + kb.DebugText);
        Pump(1.5);
        kb.SaveSnapshot(Path.Combine(outDir, "keyboard_auto.png"));

        // 「きょうは」を候補で確定 →「いいてんき」を打つ → 続きだけが変換対象になるか
        kb.DebugTap("clear");
        foreach (var k in "kyouha".Select(ch => ch.ToString())) kb.DebugTap(k);
        Pump(1.5);
        kb.DebugTap("auto:0");
        Log.Info("候補で確定: " + kb.DebugText);
        foreach (var k in "iitennki".Select(ch => ch.ToString())) kb.DebugTap(k);
        Pump(1.5);
        Log.Info("続きを入力: " + kb.DebugText);
        kb.SaveSnapshot(Path.Combine(outDir, "keyboard_auto2.png"));
        kb.DebugTap("#convert");
        Pump(1.5);
        Log.Info("変換結果: " + kb.DebugText);
        kb.SaveSnapshot(Path.Combine(outDir, "keyboard_convert.png"));
        kb.DebugTap("#convert"); // 確定
        Log.Info("確定後: " + kb.DebugText);

        // 濁点・小文字と、フリック中の表示
        kb.DebugTap("clear");
        foreach (var (k, d) in new[] { ("k", FlickDir.Right), ("a", FlickDir.Center), ("t", FlickDir.Center), ("t", FlickDir.Center), ("a", FlickDir.Center), ("h", FlickDir.Center), ("a", FlickDir.Center), ("#small", FlickDir.Center), ("#small", FlickDir.Center) })
            kb.DebugTap(k, d);
        Log.Info("入力結果(がったぱ想定): " + kb.DebugText);

        // 数字は強く引いたときだけ。弱い下フリックはキーを押したことになる
        kb.DebugTap("clear");
        kb.DebugTap("a", FlickDir.Down, reach: 0.9f); kb.DebugTap("n", FlickDir.Down, reach: 0.9f);
        kb.DebugTap("a", FlickDir.Down, reach: 0.45f);
        Log.Info("入力結果(10あ 想定): " + kb.DebugText);

        // 括弧と、割り当てのない方向へのフリック（E の下はキーを押したことになる）
        kb.DebugTap("clear");
        foreach (var (k, d) in new[] { ("y", FlickDir.Left), ("k", FlickDir.Center), ("a", FlickDir.Center), ("y", FlickDir.Right), ("w", FlickDir.Left), ("w", FlickDir.Right), ("e", FlickDir.Down), ("n", FlickDir.Up) })
            kb.DebugTap(k, d);
        Log.Info("入力結果(（か）「」え… 想定): " + kb.DebugText);

        // 途中を直す:「あいうえお」の2文字目の後ろを押して「か」を入れ、⌫ 2回で「か」と「い」を消す
        kb.DebugTap("clear");
        foreach (var k in new[] { "a", "i", "u", "e", "o" }) kb.DebugTap(k);
        Pump(0.1);
        kb.DebugTapPreview(2);
        Log.Info("カーソル移動(確定「あい」後ろ「うえお」想定): " + kb.DebugText);
        kb.DebugTap("k"); kb.DebugTap("a");
        Pump(0.1);
        kb.SaveSnapshot(Path.Combine(outDir, "keyboard_caret.png"));
        Log.Info("挿入(未確定「か」想定): " + kb.DebugText);
        kb.DebugTap("#bs"); kb.DebugTap("#bs");
        Log.Info("削除(確定「あ」後ろ「うえお」想定): " + kb.DebugText);
        kb.DebugTap("h", FlickDir.Left, hold: true);
        Pump(0.1);
        kb.SaveSnapshot(Path.Combine(outDir, "keyboard_flick.png"));
        kb.OnRelease(Hand.Right, default);

        // かなフリック:「がっこう、」→ 濁点・小文字の切り替えと記号
        kb.DebugTap("style");
        kb.DebugTap("clear");
        foreach (var (k, d) in new[] { ("か", FlickDir.Center), ("#small", FlickDir.Center), ("た", FlickDir.Up), ("#small", FlickDir.Center), ("か", FlickDir.Down), ("あ", FlickDir.Up), ("、", FlickDir.Center) })
            kb.DebugTap(k, d);
        Log.Info("入力結果(がっこう、想定): " + kb.DebugText);
        Pump(1.5);
        kb.SaveSnapshot(Path.Combine(outDir, "keyboard_kana.png"));
        kb.DebugTap("あ", FlickDir.Left, hold: true); // 左端のキー（左の候補が切れないか）
        Pump(0.1);
        kb.SaveSnapshot(Path.Combine(outDir, "keyboard_kana_flick.png"));
        kb.DebugSavePopup(Path.Combine(outDir, "keyboard_kana_flick_popup.png"));
        kb.OnRelease(Hand.Right, default);

        // QWERTY（キーボードの3つ目の配列）と、PC への直接入力（実際には送らずに記録する）
        {
            var startStyle = App.Config.Keyboard.Style;
            void StyleTo(string st) { while (App.Config.Keyboard.Style != st) kb.DebugTap("style"); }
            StyleTo("qwerty");
            kb.SetPcMode(true);
            Services.Win32Input.Record = new();
            kb.DebugTap("q:ctrl"); kb.DebugTap("q:kC");
            kb.DebugTap("q:shift"); kb.DebugTap("q:k1");
            kb.DebugTap("q:win"); kb.DebugTap("q:win");
            kb.DebugTap("q:kA"); kb.DebugTap("q:up");
            Log.Info("QWERTY→PC: " + string.Join(" ", Services.Win32Input.Record) + "（期待 Key(A2+43) Key(A0+31) Key(5B) Key(41) Key(26)）");

            Services.Win32Input.Record.Clear();
            StyleTo("flick");
            kb.DebugTap("か"); kb.DebugTap("#small"); kb.DebugTap("#convert"); kb.DebugTap("#send");
            Log.Info("フリック→PC: " + string.Join(" ", Services.Win32Input.Record) + "（期待 Key(4B) Key(41) Key(08) Key(47) Key(41) Key(20) Key(0D)）");
            Services.Win32Input.Record.Clear();
            StyleTo("godan");
            kb.DebugTap("k"); kb.DebugTap("a"); kb.DebugTap("y", FlickDir.Left); kb.DebugTap("n", FlickDir.Up);
            Log.Info("GODAN→PC: " + string.Join(" ", Services.Win32Input.Record) + "（期待 Key(4B) Key(41) Key(A0+38) Text(…)）");
            Services.Win32Input.Record = null;
            kb.SetPcMode(false);

            StyleTo("qwerty");
            kb.DebugTap("clear");
            foreach (var id in new[] { "q:kK", "q:kY", "q:kO", "q:kU" }) kb.DebugTap(id);
            Log.Info("QWERTY→入力欄: " + kb.DebugText + "（期待 未確定「きょう」）");
            Pump(1.5);
            kb.SaveSnapshot(Path.Combine(outDir, "keyboard_qwerty.png"));
            kb.DebugTap("q:enter");
            kb.DebugTap("q:shift"); kb.DebugTap("q:slash");
            Log.Info("Enter と Shift+/: " + kb.DebugText + "（期待 確定「きょう」未確定「？」）");
            kb.DebugTap("clear");
            StyleTo(startStyle);
        }

        // フェード：出るときに濃さが 0→1、消えるときに 1→0 になり、消え終わったら見えなくなるか
        {
            Panel.DebugAnimate = true;
            var m = App.Menu;
            m.Show();
            var a = new List<string>();
            for (int k = 0; k < 6; k++) { m.Flush(); a.Add($"{m.DebugAlpha:0.00}"); Thread.Sleep(50); }
            Log.Info("フェード 出る: " + string.Join(" ", a));
            a.Clear();
            m.Hide();
            for (int k = 0; k < 6; k++) { m.Flush(); a.Add($"{m.DebugAlpha:0.00}"); Thread.Sleep(50); }
            Log.Info($"フェード 消える: {string.Join(" ", a)} 消え終わったら見えない={!m.DebugShownInVr}");
            m.Show(); m.Flush(); Thread.Sleep(60); m.Hide(); m.Flush(); Thread.Sleep(40); m.Show();
            a.Clear();
            for (int k = 0; k < 5; k++) { m.Flush(); a.Add($"{m.DebugAlpha:0.00}"); Thread.Sleep(50); }
            Log.Info("フェード 消える途中でまた出す: " + string.Join(" ", a));
            m.Hide();
            for (int k = 0; k < 6; k++) { m.Flush(); Thread.Sleep(40); }
            Panel.DebugAnimate = false;
        }

        App.Watch.Show();

        // 時計で消したパネルを、頭が動いた後に呼び出すと、同じ位置関係で戻るか
        {
            App.Keyboard.Show();
            App.Keyboard.SetWorld(System.Numerics.Matrix4x4.CreateTranslation(0.1f, -0.3f, -0.5f));
            // 正面に置いたキーボードを、右を向いて消す
            App.Input.DebugSetHmd(System.Numerics.Matrix4x4.CreateRotationY(-MathF.PI / 2) * System.Numerics.Matrix4x4.CreateTranslation(0, 1.6f, 0));
            App.Keyboard.SetWorld(System.Numerics.Matrix4x4.CreateTranslation(0.1f, 1.3f, -0.5f));
            App.Watch.ButtonToggle();
            Log.Info($"時計で消す: 時計={App.Watch.Visible} キーボード={App.Keyboard.Visible}");
            // 頭が (2,1.6,3) へ移り、正面を向いて開く → 正面に出る
            App.Input.DebugSetHmd(System.Numerics.Matrix4x4.CreateTranslation(2, 1.6f, 3));
            App.Watch.ButtonToggle();
            var t = App.Keyboard.World.Translation;
            var n = App.Keyboard.World.Forward();
            var toHead = System.Numerics.Vector3.Normalize(new System.Numerics.Vector3(2 - t.X, 0, 3 - t.Z));
            float facing = System.Numerics.Vector3.Dot(-new System.Numerics.Vector3(n.X, 0, n.Z) / MathF.Max(1e-4f, new System.Numerics.Vector2(n.X, n.Z).Length()), toHead);
            Log.Info($"呼び出す: 時計={App.Watch.Visible} キーボード={App.Keyboard.Visible} 位置=({t.X:F2},{t.Y:F2},{t.Z:F2})（期待 (2.10,1.30,2.50)） 自分の方を向いているか={facing:F2}（期待 1.00）");

            App.Config.Watch.AlwaysShow = true;
            App.Watch.ButtonToggle();
            Log.Info($"常時表示で2回押し: 時計={App.Watch.Visible} キーボード={App.Keyboard.Visible}");
            App.Watch.ButtonToggle();
            Log.Info($"もう一度: 時計={App.Watch.Visible} キーボード={App.Keyboard.Visible}");
            App.Config.Watch.AlwaysShow = false;
            App.Input.DebugSetHmd(System.Numerics.Matrix4x4.Identity);
            App.Keyboard.Hide();
        }
        Pump(1.5); // 再生中の曲の情報を待つ
        App.Watch.SaveSnapshot(Path.Combine(outDir, "watch.png"));

        // ウィンドウを1つ取り込む（このPCで開いているもの）
        var wins = Services.WinApi.ListWindows();
        Log.Info("ウィンドウ: " + string.Join(" / ", wins.Take(8).Select(w => $"{w.Process}:{w.Title}")));
        var target = wins.FirstOrDefault(w => !Services.WinApi.IsMinimized(w.Hwnd));
        if (target != null)
        {
            var wp = App.AddWindow(target);
            Pump(1.5);
            wp.SaveSnapshot(Path.Combine(outDir, "window.png"));
            TouchTest(wp);
            wp.SetCurvature(0.2f);
        }

        // カーソルの絵（矢印・Iビーム・手）を白と黒の背景に並べて書き出す
        {
            var shapes = new[] { 32512, 32513, 32649 }.Select(Services.CursorImage.DebugStandard).ToArray();
            using var bmp = new SkiaSharp.SKBitmap(64 * shapes.Length, 128);
            using (var cv = new SkiaSharp.SKCanvas(bmp))
            {
                cv.Clear(SkiaSharp.SKColors.White);
                using var dark = new SkiaSharp.SKPaint { Color = new SkiaSharp.SKColor(40, 40, 40) };
                cv.DrawRect(0, 64, bmp.Width, 64, dark);
                for (int i = 0; i < shapes.Length; i++)
                {
                    if (shapes[i] is not { } s) { Log.Warn($"カーソル{i}の絵がない"); continue; }
                    Log.Info($"カーソル{i}: {s.Width}x{s.Height} ホットスポット {s.HotX},{s.HotY}");
                    var info = new SkiaSharp.SKImageInfo(s.Width, s.Height, SkiaSharp.SKColorType.Bgra8888, SkiaSharp.SKAlphaType.Unpremul);
                    using var img = SkiaSharp.SKImage.FromPixelCopy(info, s.Bgra);
                    cv.DrawImage(img, i * 64, 0);
                    cv.DrawImage(img, i * 64, 64);
                }
            }
            using var fs = File.Create(Path.Combine(outDir, "cursors.png"));
            bmp.Encode(fs, SkiaSharp.SKEncodedImageFormat.Png, 100);
        }

        // カーソルが PC の画面の中央 (960,540) に描かれるか
        Services.CursorImage.DebugPosition = (960, 540);
        App.Desktop.Show();
        Pump(1.0);
        App.Desktop.SaveSnapshot(Path.Combine(outDir, "desktop.png"));
        // モニター追加：まだ出していないモニターのパネルが隣に増える
        App.Desktop.DebugToolbar("addmonitor");
        Pump(1.0);
        var desks = App.Panels.OfType<DesktopPanel>().ToList();
        Log.Info("モニター追加: " + string.Join(" / ", desks.Select(d => $"{d.Title} 閉じられる={d.Closable}")));
        if (desks.Count > 1)
        {
            // 足したパネルでモニターを切り替えると、1枚目と入れ替わり、同じモニターが2枚に出ない
            desks[1].DebugToolbar("monitor");
            Pump(0.5);
            Log.Info($"切り替え: 1枚目={desks[0].Monitor + 1} 2枚目={desks[1].Monitor + 1}（期待 2 と 1）");
            desks[1].SaveSnapshot(Path.Combine(outDir, "desktop2.png"));
            App.Desktop.SaveSnapshot(Path.Combine(outDir, "desktop_after_add.png"));
            desks[1].Close();
        }
        App.Desktop.Hide();
        Services.CursorImage.DebugPosition = null;

        App.Menu.Show();
        foreach (var tab in new[] { "Panels", "Windows", "Volume", "Settings" })
        {
            App.Menu.DebugSetTab(tab);
            Pump(0.3);
            App.Menu.SaveSnapshot(Path.Combine(outDir, $"menu_{tab.ToLower()}.png"));
        }

        // 設定タブで値が変わるか
        App.Menu.DebugSetTab("Settings");
        App.Menu.DebugActivate("s:0:t"); App.Menu.DebugActivate("s:2:t"); App.Menu.DebugActivate("s:3:+"); App.Menu.DebugActivate("s:4:t");
        Log.Info($"設定: 時計を常に表示={App.Config.Watch.AlwaysShow} 時計の手={App.Config.Watch.Hand}/{App.Watch.AttachedHand} 間隔={App.Config.Watch.DoublePressSeconds:0.00} 通知={App.Config.Notifications.Display}（期待 True left/Left 0.50 hud）");

        App.Toast.DebugShow(new Services.NotificationWatcher.Toast("Discord", "なまえ 🌙✨ #雑談", "あとで集合ね😂👍 ワールドはいつものところで🏠 👨‍👩‍👧 ❤️ ☀️ ㈱ 한국어 ∀ ♪"));
        Pump(0.1);
        App.Toast.SaveSnapshot(Path.Combine(outDir, "toast.png"));

        foreach (var p in App.Panels.ToArray()) p.Dispose();
        Gpu.Shutdown();
        Log.Info("プレビューを書き出しました: " + outDir);
        return 0;
    }

    /// <summary>タッチ操作がどのマウス操作になるか（実際には送らずに記録する）</summary>
    static void TouchTest(Panel p)
    {
        Log.Info("タッチ入力: " + (Services.TouchInjector.Available ? "使える" : "使えない"));
        var c = new System.Numerics.Vector2(p.PixW / 2f, p.PixH / 3f);
        void Run(string name, Action act)
        {
            Services.Win32Input.Record = new();
            act();
            Log.Info($"タッチ {name}: {string.Join(" ", Services.Win32Input.Record)}");
            Services.Win32Input.Record = null;
        }
        Run("タップ", () => { p.OnPress(Hand.Right, c); p.OnDrag(Hand.Right, c); p.OnRelease(Hand.Right, c); });
        Run("Aを押しながらタップ", () =>
        {
            App.Input.Hands[(int)Hand.Right].RightClick = true;
            p.OnPress(Hand.Right, c); p.OnDrag(Hand.Right, c); p.OnRelease(Hand.Right, c);
            App.Input.Hands[(int)Hand.Right].RightClick = false;
        });
        Run("離してからタップ", () => { p.OnPress(Hand.Right, c); p.OnRelease(Hand.Right, c); });
        Run("両手", () =>
        {
            p.OnPress(Hand.Right, c); p.OnPress(Hand.Left, c + new System.Numerics.Vector2(100, 0));
            p.OnDrag(Hand.Right, c); p.OnRelease(Hand.Right, c); p.OnRelease(Hand.Left, c);
        });
    }

    /// <summary>曲面の当たり判定の計算が合っているか（端を狙うと u が 0 と 1 になるか）</summary>
    static void CurvatureTest()
    {
        var p = App.Menu;
        p.SetWorld(System.Numerics.Matrix4x4.Identity);
        float w = p.WidthMeters, c = 0.25f;
        p.SetCurvature(c);
        float theta = 2 * MathF.PI * c, r = w / theta;
        var eye = new System.Numerics.Vector3(0, 0, 0.3f);
        foreach (var (phi, expectU) in new[] { (0f, 0.5f), (theta / 2, 1f), (-theta / 2, 0f), (theta / 4, 0.75f) })
        {
            var pt = new System.Numerics.Vector3(r * MathF.Sin(phi), 0.01f, r * (1 - MathF.Cos(phi)));
            var dir = System.Numerics.Vector3.Normalize(pt - eye);
            bool ok = p.Project(eye, dir, out var px, out _, out var hit, out var n);
            Log.Info($"曲面テスト phi={phi:F2}: 当たり={ok} u={px.X / p.PixW:F3}（期待 {expectU}） 法線={n.X:F2},{n.Z:F2}");
        }
        p.SetCurvature(0);
    }
}
