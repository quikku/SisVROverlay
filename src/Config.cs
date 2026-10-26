using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;

namespace GodanOverlay;

/// <summary>overlay_config.json の中身。足りない項目は既定値で埋まる。</summary>
public sealed class Config
{
    public WatchConfig Watch { get; set; } = new();
    public KeyboardConfig Keyboard { get; set; } = new();
    public DesktopConfig Desktop { get; set; } = new();
    public OutputConfig Output { get; set; } = new();
    public ConverterConfig Converter { get; set; } = new();
    public InputConfig Input { get; set; } = new();
    public NotificationConfig Notifications { get; set; } = new();
    public UiConfig Ui { get; set; } = new();

    static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static string FilePath => Path.Combine(AppContext.BaseDirectory, "overlay_config.json");

    public static Config Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var cfg = JsonSerializer.Deserialize<Config>(File.ReadAllText(FilePath), JsonOpts);
                if (cfg != null)
                {
                    cfg.Save(); // 新しく増えた項目を書き足す
                    return cfg;
                }
            }
        }
        catch (Exception e)
        {
            Log.Error($"設定ファイルの読み込みに失敗。既定値で起動します: {e.Message}");
            try { File.Copy(FilePath, FilePath + ".broken", true); } catch { }
        }
        var def = new Config();
        def.Save();
        return def;
    }

    public void Save()
    {
        try { File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOpts)); }
        catch (Exception e) { Log.Error($"設定ファイルの保存に失敗: {e.Message}"); }
    }
}

public sealed class WatchConfig
{
    /// <summary>"right" か "left"</summary>
    public string Hand { get; set; } = "right";
    public bool ShowOnStart { get; set; } = true;
    public float WidthMeters { get; set; } = 0.10f;
    /// <summary>コントローラーから見た時計の位置と向き。時計をつかんで動かすと更新される。</summary>
    public Placement RightOffset { get; set; } = Placement.DefaultWatch(right: true);
    public Placement LeftOffset { get; set; } = Placement.DefaultWatch(right: false);
    /// <summary>トグルボタンの2回押しとみなす間隔（秒）</summary>
    public float DoublePressSeconds { get; set; } = 0.45f;
    /// <summary>時計を消さない。トグルボタンの2回押しでは、ほかのパネルだけを消したり呼び出したりする</summary>
    public bool AlwaysShow { get; set; } = false;
    /// <summary>
    /// 呼び出したパネルを、消す前の自分との位置関係に戻す（プレイスペースを動かしてずれても目の前に来る）。
    /// false なら、消す前と同じ場所（ワールド上）に戻る
    /// </summary>
    public bool RestoreRelativePosition { get; set; } = true;
}

public sealed class KeyboardConfig
{
    public float WidthMeters { get; set; } = 0.34f;
    /// <summary>"chatbox"（VRChatのChatboxへ送信）か "pc"（PCへ文字入力）</summary>
    public string DefaultOutput { get; set; } = "chatbox";
    /// <summary>フリックと判定するレーザーの移動量（キー幅に対する割合）</summary>
    public float FlickThreshold { get; set; } = 0.28f;
    /// <summary>数字のフリックと判定する移動量。トリガーを引くとレーザーが下にぶれやすいので、ほかより強く引かないと出ないようにする</summary>
    public float NumberFlickThreshold { get; set; } = 0.7f;
    public bool Haptics { get; set; } = true;
    /// <summary>"godan"、"flick"（スマホのかなフリック）、"qwerty"（日本語配列、ローマ字入力）</summary>
    public string Style { get; set; } = "flick";
    public GodanLayout Layout { get; set; } = GodanLayout.Default();
    public GodanLayout FlickLayout { get; set; } = GodanLayout.Flick();

    [JsonIgnore] public GodanLayout ActiveLayout => Style == "godan" ? Layout : FlickLayout;
}

public sealed class DesktopConfig
{
    public int MonitorIndex { get; set; } = 0;
    public float WidthMeters { get; set; } = 1.2f;
    /// <summary>VRへ送る画像の最大幅（px）。モニターの方が大きければ縮小する</summary>
    public int MaxTextureWidth { get; set; } = 1920;
    public int MaxFps { get; set; } = 60;
    public bool MoveCursorOnHover { get; set; } = true;
    /// <summary>PC のマウスカーソルを画面のパネルに描く</summary>
    public bool ShowCursor { get; set; } = true;
    /// <summary>描くカーソルの大きさ（PC の画面での大きさに対する倍率）</summary>
    public float CursorScale { get; set; } = 1.5f;
    /// <summary>トリガーを Windows のタッチとして送る（false ならマウスの左ボタン）</summary>
    public bool TouchMode { get; set; } = true;
}

public sealed class OutputConfig
{
    public string OscHost { get; set; } = "127.0.0.1";
    public int OscPort { get; set; } = 9000;
}

public sealed class ConverterConfig
{
    /// <summary>"google"（Google日本語入力 Web API）か "none"</summary>
    public string Engine { get; set; } = "google";
    public int AutoCandidateDelayMs { get; set; } = 200;
}

public sealed class InputConfig
{
    /// <summary>
    /// トリガーを引いた瞬間の手のブレを打ち消す時間（ミリ秒）。この時間だけ前にレーザーが指していた所を押したことにする。0 で無効
    /// </summary>
    public int PressStabilizeMs { get; set; } = 60;
    /// <summary>
    /// 右クリックのボタン（Quest は右手 A・左手 X）を押しながらトリガーで、PC の画面を右クリックする。
    /// どのボタンにするかは SteamVR のコントローラー設定の「右クリック（押しながらトリガー）」で変える
    /// </summary>
    public bool RightClickWithButton { get; set; } = true;
    /// <summary>レーザーがパネルに当たっている間、VRChat へのボタン入力を止める</summary>
    public bool BlockGameInputWhilePointing { get; set; } = true;
    /// <summary>レーザーの太さ（m）</summary>
    public float LaserWidthMeters { get; set; } = 0.002f;
}

public sealed class UiConfig
{
    /// <summary>パネルが出るときと消えるときにフェードする</summary>
    public bool Animations { get; set; } = true;
    /// <summary>フェードの長さ（秒）。消えるときはこれより少し短い</summary>
    public float AnimationSeconds { get; set; } = 0.2f;
}

public sealed class NotificationConfig
{
    /// <summary>Windows の通知を VR 内に出す</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>出す場所。"watch"（時計の上）、"hud"（視界の下）、"off"（出さない）。メニューでも切り替えられる</summary>
    public string Display { get; set; } = "watch";
    /// <summary>出さないアプリ（通知に表示されるアプリ名）</summary>
    public List<string> ExcludeApps { get; set; } = new();
}

/// <summary>位置 [x,y,z] と回転クォータニオン [x,y,z,w]</summary>
public sealed class Placement
{
    public float[] Position { get; set; } = [0, 0, 0];
    public float[] Rotation { get; set; } = [0, 0, 0, 1];

    public System.Numerics.Matrix4x4 ToMatrix()
    {
        var q = new System.Numerics.Quaternion(Rotation[0], Rotation[1], Rotation[2], Rotation[3]);
        if (q.LengthSquared() < 1e-6f) q = System.Numerics.Quaternion.Identity;
        var m = System.Numerics.Matrix4x4.CreateFromQuaternion(System.Numerics.Quaternion.Normalize(q));
        m.Translation = new System.Numerics.Vector3(Position[0], Position[1], Position[2]);
        return m;
    }

    public static Placement FromMatrix(System.Numerics.Matrix4x4 m)
    {
        System.Numerics.Matrix4x4.Decompose(m, out _, out var q, out var t);
        return new Placement { Position = [t.X, t.Y, t.Z], Rotation = [q.X, q.Y, q.Z, q.W] };
    }

    /// <summary>
    /// 手の甲側の手首あたりに、文字盤の上が指先を向くように置く。
    /// 実際の位置は時計をもう片方の手でつかんで調整できる。
    /// </summary>
    public static Placement DefaultWatch(bool right)
    {
        // 行 = 時計のローカル軸をコントローラー座標で表したもの
        System.Numerics.Vector3 x, y, z;
        if (right) { x = new(0, -1, 0); y = new(0, 0, -1); z = new(1, 0, 0); }
        else { x = new(0, 1, 0); y = new(0, 0, -1); z = new(-1, 0, 0); }
        var m = new System.Numerics.Matrix4x4(
            x.X, x.Y, x.Z, 0,
            y.X, y.Y, y.Z, 0,
            z.X, z.Y, z.Z, 0,
            right ? 0.035f : -0.035f, -0.03f, 0.13f, 1);
        return FromMatrix(m);
    }
}

/// <summary>
/// GODAN配列。grid の各セルはキーID（a,k,...）か操作キー（#bs,#convert,#send,#small,#clear）か null。
/// 操作キーは spans で下方向に複数行ぶん伸ばせる。
/// </summary>
public sealed class GodanLayout
{
    public string?[][] Grid { get; set; } = [];
    public Dictionary<string, int> Spans { get; set; } = new();
    /// <summary>キーごとのフリック先。英字は子音、それ以外の文字はそのまま入力される</summary>
    public Dictionary<string, FlickDef> Flicks { get; set; } = new();

    public static GodanLayout Default() => new()
    {
        Grid =
        [
            ["a", "k", "h", "#bs"],
            ["i", "s", "m", "#convert"],
            ["u", "t", "y", "#send"],
            ["e", "n", "r", null],
            ["o", "#small", "w", null],
        ],
        Spans = new() { ["#send"] = 3 },
        // 下フリックの数字は電話のテンキーと同じ並び（A=1 … Y=9、N=0）
        Flicks = new()
        {
            ["a"] = new() { Down = "1" },
            ["k"] = new() { Up = "Q", Right = "G", Down = "2" },
            ["h"] = new() { Up = "F", Right = "B", Left = "P", Down = "3" },
            ["i"] = new() { Down = "4" },
            ["s"] = new() { Up = "J", Right = "Z", Down = "5" },
            ["m"] = new() { Up = "L", Right = "ー", Down = "6" },
            ["u"] = new() { Down = "7" },
            ["t"] = new() { Up = "C", Right = "D", Down = "8" },
            ["y"] = new() { Up = "X", Left = "（", Right = "）", Down = "9" },
            ["n"] = new() { Up = "…", Down = "0" },
            ["r"] = new() { Up = "？", Right = "！", Down = "、", Left = "。" },
            ["w"] = new() { Up = "V", Left = "「", Right = "」" },
        },
    };

    /// <summary>スマホと同じ12キーのかなフリック。キーIDのかなが中央、フリック先もかなでそのまま入力される</summary>
    public static GodanLayout Flick() => new()
    {
        Grid =
        [
            ["あ", "か", "さ", "#bs"],
            ["た", "な", "は", "#convert"],
            ["ま", "や", "ら", "#send"],
            ["#small", "わ", "、", null],
        ],
        Spans = new() { ["#send"] = 2 },
        Flicks = new()
        {
            ["あ"] = new() { Left = "い", Up = "う", Right = "え", Down = "お" },
            ["か"] = new() { Left = "き", Up = "く", Right = "け", Down = "こ" },
            ["さ"] = new() { Left = "し", Up = "す", Right = "せ", Down = "そ" },
            ["た"] = new() { Left = "ち", Up = "つ", Right = "て", Down = "と" },
            ["な"] = new() { Left = "に", Up = "ぬ", Right = "ね", Down = "の" },
            ["は"] = new() { Left = "ひ", Up = "ふ", Right = "へ", Down = "ほ" },
            ["ま"] = new() { Left = "み", Up = "む", Right = "め", Down = "も" },
            ["や"] = new() { Left = "「", Up = "ゆ", Right = "」", Down = "よ" },
            ["ら"] = new() { Left = "り", Up = "る", Right = "れ", Down = "ろ" },
            ["わ"] = new() { Left = "を", Up = "ん", Right = "ー" },
            ["、"] = new() { Left = "。", Up = "？", Right = "！" },
        },
    };
}

public sealed class FlickDef
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Up { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Right { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Down { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Left { get; set; }

    public string? Get(FlickDir d) => d switch
    {
        FlickDir.Up => Up,
        FlickDir.Right => Right,
        FlickDir.Down => Down,
        FlickDir.Left => Left,
        _ => null,
    };
}

public enum FlickDir { Center, Up, Right, Down, Left }
