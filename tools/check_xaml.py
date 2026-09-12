"""Static checks for the WPF layer, standing in for a compiler this sandbox cannot run.

Verifies that every XAML file parses, that every StaticResource / DynamicResource key
exists, that every localization key used in a binding is defined in Strings.cs, and
that x:Class values line up with the code-behind.
"""
from __future__ import annotations

import re
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

APP = Path(__file__).resolve().parent.parent / "src" / "ClaudeWatch.App"
problems: list[str] = []
notes: list[str] = []


def xaml_files() -> list[Path]:
    return sorted(APP.rglob("*.xaml"))


# ---------------------------------------------------------------- well-formed
trees: dict[Path, ET.Element] = {}
for path in xaml_files():
    try:
        trees[path] = ET.parse(path).getroot()
    except ET.ParseError as exc:
        problems.append(f"{path.relative_to(APP)}: not well-formed XML — {exc}")

# ------------------------------------------------------------- resource keys
defined: set[str] = set()
for path, root in trees.items():
    for element in root.iter():
        for attribute, value in element.attrib.items():
            if attribute.endswith("}Key"):
                defined.add(value)

# Converters and built-ins declared in App.xaml are picked up above; add WPF's own.
defined |= {"BoolVis"}

used: dict[str, set[str]] = {}
pattern = re.compile(r"\{(?:Static|Dynamic)Resource\s+([A-Za-z0-9_.]+)\s*\}")
for path in xaml_files():
    text = path.read_text(encoding="utf-8")
    for key in pattern.findall(text):
        used.setdefault(key, set()).add(str(path.relative_to(APP)))

for key, where in sorted(used.items()):
    if key not in defined:
        problems.append(f"resource '{key}' is used but never defined ({', '.join(sorted(where))})")

# ------------------------------------------------------------ string catalog
strings_source = (APP / "Strings.cs").read_text(encoding="utf-8")
string_keys = set(re.findall(r'\["([A-Za-z0-9_]+)"\]\s*=', strings_source))

en_block = strings_source.split("private static readonly Dictionary<string, string> En", 1)[1]
en_block = en_block.split("private static readonly Dictionary<string, string> Fa", 1)[0]
fa_block = strings_source.split("private static readonly Dictionary<string, string> Fa", 1)[1]
en_keys = set(re.findall(r'\["([A-Za-z0-9_]+)"\]\s*=', en_block))
fa_keys = set(re.findall(r'\["([A-Za-z0-9_]+)"\]\s*=', fa_block))

for key in sorted(en_keys - fa_keys):
    notes.append(f"'{key}' has no Persian translation (falls back to English)")
for key in sorted(fa_keys - en_keys):
    problems.append(f"'{key}' exists in Persian but not English")

loc_pattern = re.compile(r"L\[([A-Za-z0-9_]+)\]")
for path in xaml_files():
    text = path.read_text(encoding="utf-8")
    for key in loc_pattern.findall(text):
        if key not in string_keys:
            problems.append(f"{path.relative_to(APP)}: text key '{key}' is not in Strings.cs")

# Keys the code asks for directly.
for source in APP.rglob("*.cs"):
    if source.name == "Strings.cs":
        continue
    for key in re.findall(r'L\["([A-Za-z0-9_]+)"\]', source.read_text(encoding="utf-8")):
        if key not in string_keys:
            problems.append(f"{source.relative_to(APP)}: text key '{key}' is not in Strings.cs")

# --------------------------------------------------------------- view models
# The view model is split across partial files.
model_source = "\n".join(
    path.read_text(encoding="utf-8") for path in sorted(APP.glob("MainViewModel*.cs"))
)
model_members = set(re.findall(r"public\s+(?:[\w<>?\[\],\s]+?)\s+(\w+)\s*(?:\{|=>|\()", model_source))
model_members |= {"L", "Edit"}

item_members = {
    # ClaudeProcess
    "Pid", "Name", "Path", "Started", "MemoryBytes", "WindowTitle", "MemoryDisplay", "StartedDisplay",
    # AdapterInfo
    "Description", "Kind", "IsUp", "LooksLikeVpn", "Trusted", "Ignored", "CountsAsVpn", "SpeedMbps", "Display",
    "CarriesTraffic", "IdleTunnel",
    # ActivityEvent
    "At", "Title", "Detail", "TimeDisplay", "DateDisplay",
    # UsageBar / UsageModel / OrderPlanChoice
    "Height", "Tooltip", "Model", "TotalTokens", "InputTokens", "OutputTokens", "Text", "Key",
    # PricedPlan
    "Popular", "Toman", "Period", "Usd", "Variable", "LabelFa", "NoteFa",
    # misc paths used inside templates
    "Foreground", "Count", "Length", "BorderBrush",
    # ServiceTab
    "Selected", "Accent", "Name",
}

# Only a positional path counts: "{Binding Foo}" or "{Binding Path=Foo}", never a
# named argument such as "{Binding Converter=...}".
binding_pattern = re.compile(r"\{Binding\s+(?:Path=)?([A-Za-z_][A-Za-z0-9_.\[\]]*)\s*(?=[,}\r\n])")

# Names that resolve against a templated control rather than the view model.
item_template_members = {"IsDropDownOpen", "SelectionBoxItem", "IsChecked", "Text"}
for path in xaml_files():
    text = path.read_text(encoding="utf-8")
    for expression in binding_pattern.findall(text):
        head = expression.split(".")[0].split("[")[0]
        if not head or head in {"DataContext", "RelativeSource"}:
            continue
        if head not in model_members and head not in item_members and head not in item_template_members:
            problems.append(f"{path.relative_to(APP)}: binding '{expression}' has no matching member")

# ------------------------------------------------------------- class wiring
for path, root in trees.items():
    class_name = None
    for attribute, value in root.attrib.items():
        if attribute.endswith("}Class"):
            class_name = value
    if class_name is None:
        continue

    namespace, _, simple = class_name.rpartition(".")
    code_behind = path.with_suffix(".xaml.cs")
    if not code_behind.exists():
        problems.append(f"{path.relative_to(APP)}: x:Class={class_name} has no code-behind file")
        continue

    source = code_behind.read_text(encoding="utf-8")
    if f"namespace {namespace}" not in source:
        problems.append(f"{code_behind.relative_to(APP)}: namespace does not match x:Class ({namespace})")
    if f"class {simple}" not in source:
        problems.append(f"{code_behind.relative_to(APP)}: class {simple} not found")

# ------------------------------------------------------------------- results
print("XAML files checked:", len(trees))
print("Resource keys defined:", len(defined), "· used:", len(used))
print("Text keys:", len(string_keys))
print()

for note in notes:
    print("  note:", note)

if problems:
    print()
    for problem in problems:
        print("  FAIL:", problem)
    print(f"\n{len(problems)} problem(s).")
    sys.exit(1)

print("No problems found.")
