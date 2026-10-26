"""Material Symbols（Rounded、太さ400）の SVG を取ってきて src/Panels/IconData.cs を作り直す。

アイコンを足すときは NAMES に名前を書き足して実行する。名前は https://fonts.google.com/icons で探す。
末尾に _fill1 を付けると塗りつぶしの版になる。
"""
import pathlib
import re
import urllib.request

NAMES = [
    # 時計の曲の操作と音量
    "skip_previous_fill1", "play_arrow_fill1", "pause_fill1", "skip_next_fill1",
    "volume_down_fill1", "volume_up_fill1", "volume_off_fill1",
    # パネルの出し入れ
    "keyboard", "desktop_windows", "menu", "center_focus_strong",
    # メニューのタブ
    "layers", "select_window", "volume_up", "settings",
    # 共通
    "close", "lock", "lock_open", "chevron_left", "chevron_right", "refresh", "add", "remove",
    # パネルの一覧
    "visibility", "visibility_off", "head_mounted_device", "panorama_horizontal", "crop_landscape", "back_hand",
    # キーボード
    "backspace", "send", "chat", "record_voice_over", "computer", "delete_sweep", "language",
    # PCの画面
    "monitor", "add_to_queue",
]

URL = "https://raw.githubusercontent.com/google/material-design-icons/master/symbols/web/{base}/materialsymbolsrounded/{name}_24px.svg"


def fetch(name: str) -> str:
    base = name.removesuffix("_fill1")
    svg = urllib.request.urlopen(URL.format(base=base, name=name)).read().decode()
    paths = re.findall(r'<path d="([^"]+)"', svg)
    if len(paths) != 1:
        raise ValueError(f"{name}: path が {len(paths)} 個")
    return paths[0]


def main() -> None:
    root = pathlib.Path(__file__).resolve().parents[2]
    lines = [
        "// third_party/material-symbols/fetch_icons.py が作るファイル。手で書き換えない",
        "// Material Symbols (Apache License 2.0) https://github.com/google/material-design-icons",
        "namespace GodanOverlay.Panels;",
        "",
        "static class IconData",
        "{",
        "    /// <summary>アイコンの名前と SVG のパス（viewBox は 0 -960 960 960）</summary>",
        "    public static readonly Dictionary<string, string> Paths = new()",
        "    {",
    ]
    for name in NAMES:
        lines.append(f'        ["{name}"] = "{fetch(name)}",')
    lines += ["    };", "}", ""]
    out = root / "src" / "Panels" / "IconData.cs"
    out.write_text("\n".join(lines), encoding="utf-8")
    print(f"{out} ({len(NAMES)} 個)")


if __name__ == "__main__":
    main()
