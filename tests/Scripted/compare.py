"""Compares the drawing dumps of the scripted end-to-end tests.

    venv\\Scripts\\python.exe -I tests\\Scripted\\compare.py --expected tests\\Scripted\\expected --dumps <dump folder>
        --scenarios a,b --cads acad,zwcad [--report report.txt] [--tol 0.01]

(i)  every dump <scenario>.<cad>.json is checked against expected/<scenario>.json: reviewed assertions, each
     {"name", "where": {...}, "count": n | "min": n | "max": n}. `where` is a partial match on the dumped entity
     (type, layer, block, line, vertices, pos, dyn, text, xdata ...); numbers match within the tolerance, lists
     element by element, strings exactly, or as a regular expression when they start with "re:". A check needs
     at least one match unless it gives count, min or max. Other check kinds: {"name", "xml": {"row": [cells...]}}
     for a workbook dump <scenario>.<cad>.xml (see check_xml).
(ii) with two or more CADs, the dumps of the same scenario are compared entity by entity (tolerance 0.01 mm) and
     every difference is reported.

Output: PASS / FAIL / DIFF lines and a SUMMARY line; exit code 0 when nothing failed.
"""
import argparse
import json
import re
import sys
import xml.etree.ElementTree as ET
from pathlib import Path


def num(value):
    return isinstance(value, (int, float)) and not isinstance(value, bool)


def equal(expected, actual, tol):
    if num(expected) and num(actual):
        return abs(expected - actual) <= tol
    if isinstance(expected, list) and isinstance(actual, list):
        return len(expected) == len(actual) and all(equal(e, a, tol) for e, a in zip(expected, actual))
    if isinstance(expected, dict) and isinstance(actual, dict):
        return all(k in actual and equal(v, actual[k], tol) for k, v in expected.items())
    if isinstance(expected, str) and isinstance(actual, str):
        if expected.startswith("re:"):
            return re.search(expected[3:], actual) is not None
        return expected == actual
    return expected == actual


def matches(entity, where, tol):
    for key, expected in where.items():
        if expected is None:
            if key in entity:
                return False
            continue
        if key not in entity or not equal(expected, entity[key], tol):
            return False
    return True


def entity_text(entity):
    return json.dumps(entity, sort_keys=True, ensure_ascii=False)


class Results:
    def __init__(self, report, known=None, strict=False):
        self.passed = 0
        self.failed = 0
        self.known = 0
        self.lines = []
        self.report = report
        self.known_list = known or []
        self.strict = strict
        self.current = None  # (scenario, cad) of the checks being run

    def reason(self, text):
        """The reason of a known failure that matches this failing check, or None."""
        if self.strict or self.current is None:
            return None
        for entry in self.known_list:
            if entry["scenario"] == self.current[0] and entry["cad"] == self.current[1]                     and re.search(entry["match"], text):
                return entry["reason"]
        return None

    def ok(self, text):
        self.passed += 1
        self.emit("PASS " + text)

    def fail(self, text):
        reason = self.reason(text)
        if reason is not None:
            self.known += 1
            self.emit("KNOWN %s [%s]" % (text, reason))
            return
        self.failed += 1
        self.emit("FAIL " + text)

    def info(self, text):
        self.emit(text)

    def emit(self, line):
        self.lines.append(line)
        print(line)


def run_checks(results, label, entities, expected, tol):
    for check in expected.get("checks", []):
        name = "%s :: %s" % (label, check["name"])
        if "orthogonal" in check:
            # every consecutive vertex pair of the matching polylines is parallel to an axis (or, with
            # "allow_45", also at 45 degrees)
            where = check["orthogonal"]
            polys = [e for e in entities if "vertices" in e and matches(e, where, tol)]
            bad = []
            for e in polys:
                v = e["vertices"]
                for a, b in zip(v, v[1:]):
                    dx, dy = abs(a[0] - b[0]), abs(a[1] - b[1])
                    if not (dx <= tol or dy <= tol or (check.get("allow_45") and abs(dx - dy) <= 0.05)):
                        bad.append((a[:2], b[:2]))
            if polys and not bad:
                results.ok("%s (%d polylines checked)" % (name, len(polys)))
            else:
                results.fail("%s: %s" % (name, "no polylines match" if not polys else "off-axis segments %s" % bad[:3]))
            continue
        if "where" not in check:
            continue
        found = [e for e in entities if matches(e, check["where"], tol)]
        n = len(found)
        if "count" in check:
            good, want = n == check["count"], "count == %s" % check["count"]
        elif "min" in check or "max" in check:
            good = n >= check.get("min", 0) and n <= check.get("max", 10 ** 9)
            want = "count in [%s, %s]" % (check.get("min", 0), check.get("max", "inf"))
        else:
            good, want = n >= 1, "at least one match"
        if good:
            results.ok("%s (%d match%s)" % (name, n, "" if n == 1 else "es"))
        else:
            results.fail("%s: wanted %s, found %d for %s" % (name, want, n, json.dumps(check["where"], ensure_ascii=False)))
            near = [e for e in entities if all(e.get(k) == check["where"].get(k) for k in ("type", "layer", "block") if k in check["where"])]
            for e in near[:4]:
                results.info("     candidate: " + entity_text(e))


def read_xml_rows(path):
    """Rows of a SpreadsheetML workbook: {sheet name: [[cell text, ...], ...]}."""
    ns = {"ss": "urn:schemas-microsoft-com:office:spreadsheet"}
    root = ET.parse(path).getroot()
    sheets = {}
    for ws in root.findall("ss:Worksheet", ns):
        name = ws.get("{urn:schemas-microsoft-com:office:spreadsheet}Name")
        rows = []
        for row in ws.findall("ss:Table/ss:Row", ns):
            cells = []
            for cell in row.findall("ss:Cell", ns):
                index = cell.get("{urn:schemas-microsoft-com:office:spreadsheet}Index")
                if index:
                    while len(cells) < int(index) - 1:
                        cells.append("")
                data = cell.find("ss:Data", ns)
                cells.append("" if data is None or data.text is None else data.text)
                merge = cell.get("{urn:schemas-microsoft-com:office:spreadsheet}MergeAcross")
                if merge:
                    cells.extend([""] * int(merge))
            rows.append(cells)
        sheets[name] = rows
    return sheets


def check_xml(results, label, sheets, expected, tol):
    for check in expected.get("xml_checks", []):
        name = "%s :: %s" % (label, check["name"])
        rows = sheets.get(check["sheet"], [])
        want = check["row"]
        hit = None
        for row in rows:
            if len(row) >= len(want) and all(
                w is None or (str(w).startswith("re:") and re.search(str(w)[3:], row[i]) is not None)
                or (num(w) and _float(row[i]) is not None and abs(_float(row[i]) - w) <= tol)
                or (isinstance(w, str) and not w.startswith("re:") and row[i] == w)
                for i, w in enumerate(want)):
                hit = row
                break
        if hit is not None:
            results.ok(name)
        else:
            results.fail("%s: no row like %s on sheet %s" % (name, json.dumps(want, ensure_ascii=False), check["sheet"]))


def _float(text):
    try:
        return float(text)
    except (TypeError, ValueError):
        return None


def cross_compare(results, scenario, dumps, tol):
    """Entity by entity, greedy matching: every entity of one dump needs an equal one in the other."""
    cads = list(dumps)
    base_cad = cads[0]
    for other in cads[1:]:
        a, b = dumps[base_cad]["entities"], dumps[other]["entities"]
        label = "%s :: %s vs %s" % (scenario, base_cad, other)
        left = list(b)
        only_a = []
        for e in a:
            hit = next((i for i, f in enumerate(left) if equal_entity(e, f, tol)), None)
            if hit is None:
                only_a.append(e)
            else:
                left.pop(hit)
        if not only_a and not left:
            results.ok("%s: %d entities identical within %.2f" % (label, len(a), tol))
            continue
        results.fail("%s: %d entities only in %s, %d only in %s" % (label, len(only_a), base_cad, len(left), other))
        for e in only_a[:12]:
            results.info("DIFF only in %s: %s" % (base_cad, entity_text(e)))
        for e in left[:12]:
            results.info("DIFF only in %s: %s" % (other, entity_text(e)))
        pairs = 0
        for e in only_a:
            twin = next((f for f in left if f.get("type") == e.get("type") and f.get("layer") == e.get("layer")
                         and f.get("block") == e.get("block")), None)
            if twin is not None and pairs < 8:
                pairs += 1
                diff = [k for k in sorted(set(e) | set(twin)) if not equal(e.get(k), twin.get(k), tol)]
                results.info("DIFF nearest pair differs in: %s" % ", ".join(diff))


def equal_entity(a, b, tol):
    if set(a) != set(b):
        return False
    return all(equal(a[k], b[k], tol) and equal(b[k], a[k], tol) for k in a)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--expected", required=True)
    parser.add_argument("--dumps", required=True)
    parser.add_argument("--scenarios", required=True)
    parser.add_argument("--cads", default="acad")
    parser.add_argument("--report", default="")
    parser.add_argument("--tol", type=float, default=0.01)
    parser.add_argument("--known", default="", help="known-failures.json: failures that are listed there are reported as KNOWN")
    parser.add_argument("--strict", action="store_true", help="count known failures as failures")
    args = parser.parse_args()
    sys.stdout.reconfigure(encoding="utf-8")

    known = json.loads(Path(args.known).read_text(encoding="utf-8")) if args.known and Path(args.known).exists() else []
    results = Results(args.report, known, args.strict)
    cads = [c for c in args.cads.split(",") if c]
    for scenario in [s for s in args.scenarios.split(",") if s]:
        results.info("== " + scenario)
        exp_path = Path(args.expected) / (scenario + ".json")
        expected = json.loads(exp_path.read_text(encoding="utf-8")) if exp_path.exists() else None
        if expected is None:
            results.fail("%s: no expectations file %s" % (scenario, exp_path))
        dumps = {}
        for cad in cads:
            path = Path(args.dumps) / ("%s.%s.json" % (scenario, cad))
            results.current = (scenario, cad)
            if not path.exists():
                results.fail("%s [%s]: no dump (the run did not reach HVACTESTDUMP)" % (scenario, cad))
                continue
            dump = json.loads(path.read_text(encoding="utf-8"))
            dumps[cad] = dump
            results.current = (scenario, cad)
            if expected is not None:
                run_checks(results, "%s [%s]" % (scenario, cad), dump["entities"], expected, args.tol)
                xml_path = Path(args.dumps) / ("%s.%s.xml" % (scenario, cad))
                if expected.get("xml_checks"):
                    if xml_path.exists():
                        check_xml(results, "%s [%s]" % (scenario, cad), read_xml_rows(xml_path), expected, args.tol)
                    else:
                        results.fail("%s [%s]: no workbook %s" % (scenario, cad, xml_path.name))
        if len(dumps) >= 2:
            results.current = (scenario, "cross")
            cross_compare(results, scenario, dumps, args.tol)

    results.info("SUMMARY passed=%d failed=%d known=%d" % (results.passed, results.failed, results.known))
    if args.report:
        Path(args.report).write_text("\n".join(results.lines) + "\n", encoding="utf-8")
    return 0 if results.failed == 0 else 1


if __name__ == "__main__":
    sys.exit(main())
