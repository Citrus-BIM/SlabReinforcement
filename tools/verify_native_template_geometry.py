"""Independent standard-library audit of native Revit axes (millimetres).

No NTS/Shapely, engineering anchorage, lap or collision claims. Lines use flat
cylinder ends. Vertical circular arcs have analytic axis extrema and exact Z
extrema of the swept circular section. Their XY capsule is a conservative bound:
if that bound fails, the outcome is UNKNOWN unless an axis violation proves FAIL.
"""
import argparse
import collections
import hashlib
import json
import math
import sys
import unittest
from pathlib import Path

EPS = 1e-7                    # numerical geometry predicate in mm
COVER_EPS = 1e-5              # floating-point allowance, not a modelling tolerance
MATCH_EPS = 1e-5              # full geometry save/reopen and duplicate comparison


def vec(p, dimension=3):
    if not isinstance(p, (list, tuple)) or len(p) < dimension:
        raise ValueError("missing coordinate")
    q = tuple(float(v) for v in p[:dimension])
    if not all(math.isfinite(v) for v in q):
        raise ValueError("nonfinite coordinate")
    return q


def sub(a, b): return tuple(x-y for x, y in zip(a, b))
def add(a, b): return tuple(x+y for x, y in zip(a, b))
def mul(a, k): return tuple(x*k for x in a)
def dot(a, b): return sum(x*y for x, y in zip(a, b))
def norm(a): return math.sqrt(dot(a, a))
def cross(a, b): return (a[1]*b[2]-a[2]*b[1], a[2]*b[0]-a[0]*b[2], a[0]*b[1]-a[1]*b[0])
def cross2(a, b): return a[0]*b[1]-a[1]*b[0]
def edges(ring): return zip(ring, ring[1:] + ring[:1])


def point_segment_distance(p, a, b):
    p, a, b = vec(p, 2), vec(a, 2), vec(b, 2)
    ab = sub(b, a)
    if dot(ab, ab) <= EPS*EPS:
        return norm(sub(p, a))
    t = max(0.0, min(1.0, dot(sub(p, a), ab)/dot(ab, ab)))
    return norm(sub(p, add(a, mul(ab, t))))


def point_in_ring(point, ring):
    p = vec(point, 2)
    winding = 0
    for a, b in edges(ring):
        a, b = vec(a, 2), vec(b, 2)
        if point_segment_distance(p, a, b) <= EPS:
            return True
        side = cross2(sub(b, a), sub(p, a))
        if a[1] <= p[1] < b[1] and side > 0:
            winding += 1
        elif b[1] <= p[1] < a[1] and side < 0:
            winding -= 1
    return winding != 0


def segment_distance(a, b, c, d):
    a, b, c, d = (vec(p, 2) for p in (a, b, c, d))
    ab, cd = sub(b, a), sub(d, c)
    s1, s2 = cross2(ab, sub(c, a)), cross2(ab, sub(d, a))
    t1, t2 = cross2(cd, sub(a, c)), cross2(cd, sub(b, c))
    if ((s1 > 0 and s2 < 0) or (s1 < 0 and s2 > 0)) and ((t1 > 0 and t2 < 0) or (t1 < 0 and t2 > 0)):
        return 0.0
    result = min(point_segment_distance(a, c, d), point_segment_distance(b, c, d),
                 point_segment_distance(c, a, b), point_segment_distance(d, a, b))
    return 0.0 if result <= EPS else result


def inside_host(p, shell, holes=()):
    return point_in_ring(p, shell) and not any(point_in_ring(p, h) for h in holes)


def segment_clearance(a, b, shell, holes=()):
    if not inside_host(a, shell, holes) or not inside_host(b, shell, holes):
        return -1.0
    return min(segment_distance(a, b, c, d) for ring in [shell, *holes] for c, d in edges(ring))


def straight_side_cover(a, b, radius, shell, holes=()):
    a, b = vec(a), vec(b)
    delta = sub(b, a)
    length = norm(delta)
    if length <= EPS:
        raise ValueError("zero length 3D line")
    xylen = norm(delta[:2])
    if xylen <= EPS:
        return segment_clearance(a, a, shell, holes) - radius
    if abs(delta[2]) > EPS:
        raise ValueError("inclined cylinder side projection unsupported")
    n = (-delta[1]/xylen*radius, delta[0]/xylen*radius)
    polygon = [add(a[:2], n), add(b[:2], n), sub(b[:2], n), sub(a[:2], n)]
    if any(point_in_ring(h[0], polygon) for h in holes):
        return -1.0
    return min(segment_clearance(c, d, shell, holes) for c, d in edges(polygon))


def angular_extrema(u, v, sweep):
    candidates = [0.0, sweep]
    theta = math.atan2(v, u) % (2*math.pi)
    for t in (theta, (theta+math.pi) % (2*math.pi)):
        if t <= sweep + 1e-10:
            candidates.append(t)
    values = [u*math.cos(t)+v*math.sin(t) for t in candidates]
    return min(values), max(values)


def arc_geometry(curve, tube_radius=0.0):
    a, m, b = (vec(curve[key]) for key in ("start", "midpoint", "end"))
    ab, ac = sub(m, a), sub(b, a)
    nraw = cross(ab, ac)
    n2 = dot(nraw, nraw)
    if n2 <= EPS*EPS:
        raise ValueError("degenerate three-point arc")
    center = add(a, mul(add(mul(cross(nraw, ab), dot(ac, ac)), mul(cross(ac, nraw), dot(ab, ab))), 1/(2*n2)))
    radius = norm(sub(a, center))
    if radius <= tube_radius or not math.isfinite(radius):
        raise ValueError("invalid arc radius")
    n = mul(nraw, 1/math.sqrt(n2))
    if abs(n[2]) > 1e-8:
        raise ValueError("arc plane is not vertical: XY is not a segment")
    if curve.get("centerMm") is not None and norm(sub(vec(curve["centerMm"]), center)) > 1e-4:
        raise ValueError("reported arc center inconsistent with three points")
    if curve.get("radiusMm") is not None and abs(float(curve["radiusMm"])-radius) > 1e-4:
        raise ValueError("reported arc radius inconsistent with three points")
    if curve.get("normal") is not None:
        reported = vec(curve["normal"])
        if abs(abs(dot(reported, n))-1) > 1e-7:
            raise ValueError("reported arc normal inconsistent with plane")
    u = mul(sub(a, center), 1/radius)
    v = cross(n, u)
    angle = lambda p: math.atan2(dot(sub(p, center), v), dot(sub(p, center), u)) % (2*math.pi)
    sweep, middle = angle(b), angle(m)
    if sweep <= 1e-10 or middle > sweep + 1e-8 or abs(middle-sweep/2) > 1e-6:
        raise ValueError("midpoint is not the middle of the specified circular arc")
    if curve.get("lengthMm") is not None and abs(float(curve["lengthMm"])-radius*sweep) > 1e-4:
        raise ValueError("reported arc length inconsistent with sweep")
    h = (-n[1], n[0], 0.0)
    lo, hi = angular_extrema(dot(u, h), dot(v, h), sweep)
    xy = (add(center[:2], mul(h[:2], radius*lo)), add(center[:2], mul(h[:2], radius*hi)))
    zlo, zhi = angular_extrema(u[2], v[2], sweep)
    zs = [center[2] + r*z for r in (radius-tube_radius, radius+tube_radius) for z in (zlo, zhi)]
    return {"xy": xy, "z": (min(zs), max(zs)), "center": center, "radius": radius, "sweep": sweep}

def curve_equal(a, b, tolerance=MATCH_EPS):
    if a.get("kind") != b.get("kind"):
        return False
    near = lambda p, q: norm(sub(vec(p), vec(q))) <= tolerance
    return near(a["midpoint"], b["midpoint"]) and (
        (near(a["start"], b["start"]) and near(a["end"], b["end"])) or
        (near(a["start"], b["end"]) and near(a["end"], b["start"])))


def same_bar(a, b, tolerance=MATCH_EPS):
    if abs(float(a["modelDiameterMm"])-float(b["modelDiameterMm"])) > tolerance:
        return False
    remaining = list(b["curves"])
    if len(a["curves"]) != len(remaining):
        return False
    for curve in a["curves"]:
        match = next((i for i, c in enumerate(remaining) if curve_equal(curve, c, tolerance)), None)
        if match is None:
            return False
        remaining.pop(match)
    return True


def duplicate_bars(bars):
    buckets = collections.defaultdict(list)
    duplicates = []
    for i, bar in enumerate(bars):
        points = [vec(c[k]) for c in bar["curves"] for k in ("start", "end", "midpoint")]
        key = tuple(math.floor(min(p[k] for p in points)) for k in range(3))
        # Adjacent 1 mm buckets prevent rounding-boundary misses at the 1e-5 mm comparison.
        for dx in (-1, 0, 1):
            for dy in (-1, 0, 1):
                for dz in (-1, 0, 1):
                    for j in buckets[(key[0]+dx, key[1]+dy, key[2]+dz)]:
                        if same_bar(bar, bars[j]):
                            duplicates.append([j, i])
        buckets[key].append(i)
    return duplicates


def bar_identity(bar):
    return (bar["systemId"], bar["setId"], bar["position"])


def free_endpoints(curves):
    nodes, links = [], []
    for curve in curves:
        ids = []
        for key in ("start", "end"):
            p = vec(curve[key])
            node = next((i for i, q in enumerate(nodes) if norm(sub(p, q)) <= MATCH_EPS), None)
            if node is None:
                node = len(nodes)
                nodes.append(p)
            ids.append(node)
        links.append(ids)
    degree = collections.Counter(i for link in links for i in link)
    ends = [i for i in degree if degree[i] == 1]
    seen, todo = set(), [0]
    while todo:
        i = todo.pop()
        if i in seen: continue
        seen.add(i)
        todo.extend(y if x == i else x for x, y in links if x == i or y == i)
    if len(ends) != 2 or len(seen) != len(nodes) or any(d > 2 for d in degree.values()):
        raise ValueError("disconnected, branched, repeated or closed U axis")
    return [nodes[i] for i in ends]


def audit_geometry(data, metadata, expected_edges=14, max_spacing=200.0, cover=35.0):
    issues = []
    def issue(code, detail, level="FAIL", bar=None):
        issues.append({"level": level, "code": code, "detail": detail,
                       **({"bar": list(bar_identity(bar))} if bar is not None else {})})
    shell = [vec(p, 2) for p in data["Shell"]]
    if len(shell) > 1 and norm(sub(shell[0], shell[-1])) <= EPS: shell.pop()
    holes = [[vec(p, 2) for p in h] for h in data.get("Holes", [])]
    if len(shell) != expected_edges:
        issue("HOST_EDGE_COUNT", {"actual": len(shell), "expected": expected_edges})
    if len(shell) < 3: raise ValueError("host ring has fewer than three vertices")
    signed_area = sum(cross2(a, b) for a, b in edges(shell))/2
    if abs(signed_area) <= EPS: raise ValueError("zero area host")
    top, thickness = float(data["TopElevationMm"]), float(data["ThicknessMm"])
    if not all(math.isfinite(x) for x in (top, thickness, cover, max_spacing)) or thickness <= 0 or cover <= 0 or max_spacing <= 0:
        raise ValueError("invalid host/check parameters")
    if "CoverMm" in data and abs(float(data["CoverMm"])-cover) > COVER_EPS:
        issue("COVER_INPUT", {"export": data["CoverMm"], "expected": cover})
    bars = data["Bars"]
    if not bars: issue("EMPTY_EXPORT", "No physical bars")
    paths = metadata.get("pathEdges", [])
    path_map = {}
    edge_indices = []
    for p in paths:
        try:
            j = int(p["EdgeIndex"])
            if j < 0 or j >= len(shell): raise ValueError("invalid edge index")
            a, b = shell[j], shell[(j+1) % len(shell)]
            tangent = mul(sub(b, a), 1/norm(sub(b, a)))
            sign = 1 if signed_area > 0 else -1
            inward = (-tangent[1]*sign, tangent[0]*sign)
            if norm(sub(vec(p["SourceStart"], 2), a)) > MATCH_EPS or norm(sub(vec(p["SourceEnd"], 2), b)) > MATCH_EPS:
                issue("PATH_SOURCE_EDGE", j)
            if norm(sub(vec(p["Tangent"], 2), tangent)) > 1e-7 or norm(sub(vec(p["Inward"], 2), inward)) > 1e-7:
                issue("PATH_EDGE_ORIENTATION", j)
            if p["Id"] in path_map: issue("REPEATED_PATH_ID", p["Id"])
            path_map[p["Id"]] = {"edge": j, "a": a, "b": b, "t": tangent, "n": inward, "stations": [], "legs": []}
            edge_indices.append(j)
        except (KeyError, TypeError, ValueError) as error:
            issue("INVALID_EDGE_METADATA", str(error), "UNKNOWN")
    if sorted(edge_indices) != list(range(expected_edges)):
        issue("EDGE_MAPPING_INCOMPLETE", {"indices": edge_indices, "expected": expected_edges})
    expected_layers = {"Низ X": (-147.0, 0), "Низ Y": (-135.0, 1), "Верх X": (-53.0, 0), "Верх Y": (-65.0, 1)}
    area_systems = collections.defaultdict(set)
    layer_z = collections.defaultdict(list)
    set_stations = collections.defaultdict(list)
    identities, valid_bars = set(), []
    minima = {"sideCoverLowerBoundMm": math.inf, "topCoverMm": math.inf, "bottomCoverMm": math.inf}
    arc_count = 0
    for bar in bars:
        try:
            identity = bar_identity(bar)
            if identity in identities: issue("DUPLICATE_POSITION_DATA", identity, bar=bar)
            identities.add(identity)
            diameter = float(bar["modelDiameterMm"])
            if not math.isfinite(diameter) or diameter <= 0: raise ValueError("invalid physical diameter")
            if abs(diameter-12) > COVER_EPS or abs(float(bar["nominalDiameterMm"])-12) > COVER_EPS:
                issue("UNEXPECTED_DIAMETER", diameter, bar=bar)
            radius = diameter/2
            if bar["layout"] not in ("MaximumSpacing", "Single"):
                issue("LAYOUT_RULE", bar["layout"], bar=bar)
            curves = bar["curves"]
            if not curves: raise ValueError("empty curve list")
            all_points = []
            for curve in curves:
                a, m, b = (vec(curve[k]) for k in ("start", "midpoint", "end"))
                all_points.extend((a, m, b))
                if curve["kind"] == "Line":
                    delta = sub(b, a)
                    length = norm(delta)
                    if length <= EPS: raise ValueError("zero-length line")
                    if norm(sub(m, mul(add(a, b), .5))) > MATCH_EPS: raise ValueError("line midpoint mismatch")
                    if curve.get("lengthMm") is not None and abs(float(curve["lengthMm"])-length) > 1e-4: raise ValueError("line length mismatch")
                    side = straight_side_cover(a, b, radius, shell, holes)
                    radial_z = radius*math.sqrt(max(0, 1-(delta[2]/length)**2))
                    zmin, zmax = min(a[2], b[2])-radial_z, max(a[2], b[2])+radial_z
                    if side < cover-COVER_EPS: issue("SIDE_COVER", side, bar=bar)
                elif curve["kind"] == "Arc":
                    arc_count += 1
                    ag = arc_geometry(curve, radius)
                    zmin, zmax = ag["z"]
                    axis_side = segment_clearance(*ag["xy"], shell, holes)
                    side = axis_side-radius
                    if side < cover-COVER_EPS:
                        level = "FAIL" if axis_side < cover-COVER_EPS else "UNKNOWN"
                        issue("ARC_SIDE_COVER" if level == "FAIL" else "ARC_CAPSULE_BOUND_INCONCLUSIVE", {"axisClearanceMm": axis_side, "conservativeCoverMm": side}, level, bar)
                else:
                    raise ValueError("unsupported curve kind "+str(curve["kind"]))
                top_cover, bottom_cover = top-zmax, zmin-(top-thickness)
                minima["sideCoverLowerBoundMm"] = min(minima["sideCoverLowerBoundMm"], side)
                minima["topCoverMm"] = min(minima["topCoverMm"], top_cover)
                minima["bottomCoverMm"] = min(minima["bottomCoverMm"], bottom_cover)
                if min(top_cover, bottom_cover) < cover-COVER_EPS:
                    issue("VERTICAL_COVER", {"topMm": top_cover, "bottomMm": bottom_cover}, bar=bar)
            if bar["systemKind"] == "Area":
                name = bar["layer"]
                area_systems[name].add(bar["systemId"])
                if name not in expected_layers: raise ValueError("unrecognized Area layer "+str(name))
                z, direction = expected_layers[name]
                if len(curves) != 1 or curves[0]["kind"] != "Line": raise ValueError("expected one straight curve for Area bar")
                a, b = vec(curves[0]["start"]), vec(curves[0]["end"])
                delta = sub(b, a)
                if max(abs(delta[1-direction]), abs(delta[2])) > MATCH_EPS:
                    issue("AREA_DIRECTION", delta, bar=bar)
                local_z = [p[2]-top for p in all_points]
                layer_z[name].extend(local_z)
                if max(abs(v-z) for v in local_z) > MATCH_EPS:
                    issue("AREA_LAYER_Z", {"actual": local_z, "expected": z}, bar=bar)
                station = (a[1-direction]+b[1-direction])/2
            elif bar["systemKind"] == "Path":
                p = path_map.get(bar["systemId"])
                if p is None: raise ValueError("Path has no source edge metadata")
                lines = [c for c in curves if c["kind"] == "Line"]
                arcs = [c for c in curves if c["kind"] == "Arc"]
                if len(lines) != 3 or len(arcs) != 2: raise ValueError("expected 3 lines and 2 arcs in U")
                webs = [c for c in lines if norm(sub(vec(c["end"], 2), vec(c["start"], 2))) <= EPS]
                if len(webs) != 1: raise ValueError("expected exactly one vertical U web")
                web = vec(webs[0]["start"], 2)
                station = dot(sub(web, p["a"]), p["t"])
                inset = dot(sub(web, p["a"]), p["n"])
                if abs(inset-cover-radius) > MATCH_EPS: issue("U_WEB_INSET", inset, bar=bar)
                if station < -MATCH_EPS or station > norm(sub(p["b"], p["a"]))+MATCH_EPS:
                    issue("U_BEYOND_SOURCE_EDGE", station, bar=bar)
                if any(abs(dot(sub(q[:2], web), p["t"])) > MATCH_EPS for q in all_points):
                    issue("U_NOT_TRANSVERSE_TO_EDGE", p["edge"], bar=bar)
                ends = free_endpoints(curves)
                projections = [dot(sub(q[:2], web), p["n"]) for q in ends]
                if min(projections) <= EPS: issue("U_OUTWARD_TAIL", projections, bar=bar)
                if max(abs(min(q[2] for q in all_points)-top+159), abs(max(q[2] for q in all_points)-top+41)) > MATCH_EPS:
                    issue("U_AXIS_HEIGHT", [q[2]-top for q in ends], bar=bar)
                p["stations"].append(station)
                p["legs"].extend(projections)
            else:
                raise ValueError("unknown reinforcement system kind")
            set_stations[(bar["systemId"], bar["setId"])].append(station)
            valid_bars.append(bar)
        except (KeyError, TypeError, ValueError, ZeroDivisionError) as error:
            issue("INVALID_OR_UNSUPPORTED_GEOMETRY", str(error), "UNKNOWN", bar)
    if set(area_systems) != set(expected_layers) or any(len(ids) != 1 for ids in area_systems.values()) or len(set.union(set(), *area_systems.values())) != 4:
        issue("FOUR_DISTINCT_AREA_SYSTEMS", {k: sorted(v) for k, v in area_systems.items()})
    maximum_gap = 0.0
    for key, stations in set_stations.items():
        values = sorted(stations)
        gap = max((b-a for a, b in zip(values, values[1:])), default=0)
        maximum_gap = max(maximum_gap, gap)
        if gap > max_spacing+MATCH_EPS: issue("SET_MAX_SPACING", {"set": key, "gapMm": gap})
    edge_report = []
    for key, p in sorted(path_map.items(), key=lambda item: item[1]["edge"]):
        stations = sorted(p["stations"])
        length = norm(sub(p["b"], p["a"]))
        if not stations:
            issue("EDGE_HAS_NO_U", p["edge"])
            continue
        gap = max((b-a for a, b in zip(stations, stations[1:])), default=0)
        maximum_gap = max(maximum_gap, gap)
        end_gaps = [stations[0], length-stations[-1]]
        if gap > max_spacing+MATCH_EPS: issue("EDGE_MAX_SPACING", {"edge": p["edge"], "gapMm": gap})
        if max(end_gaps) > max_spacing+MATCH_EPS: issue("UNCOVERED_EDGE_END", {"edge": p["edge"], "endGapsMm": end_gaps})
        edge_report.append({"edgeIndex": p["edge"], "systemId": key, "physicalBars": len(stations), "sourceLengthMm": length,
                            "firstStationMm": stations[0], "lastStationMm": stations[-1], "maxGapMm": gap, "endGapsMm": end_gaps,
                            "axisLegProjectionRangeMm": [min(p["legs"]), max(p["legs"])]})
    duplicates = duplicate_bars(valid_bars)
    for a, b in duplicates: issue("DUPLICATE_FULL_BAR", [list(bar_identity(valid_bars[a])), list(bar_identity(valid_bars[b]))])
    if data.get("PhysicalBars") != len(bars): issue("EXPORT_PHYSICAL_COUNT", {"reported": data.get("PhysicalBars"), "actual": len(bars)})
    if data.get("Sets") != len({(b["systemId"], b["setId"]) for b in bars}): issue("EXPORT_SET_COUNT", data.get("Sets"))
    status = "FAIL" if any(i["level"] == "FAIL" for i in issues) else "UNKNOWN" if issues else "PASS"
    return {"status": status, "physicalBars": len(bars), "checkedBars": len(valid_bars), "arcCount": arc_count,
            "sets": len(set_stations), "areaSystems": {k: sorted(v) for k, v in area_systems.items()},
            "layerAxisZRangesMm": {k: [min(v), max(v)] for k, v in layer_z.items()}, "pathEdges": edge_report,
            "minimum": {k: v if math.isfinite(v) else None for k, v in minima.items()}, "maxActualGapMm": maximum_gap,
            "duplicateFullBars": len(duplicates), "issueCount": len(issues), "issues": issues}

def fixture():
    shell = [[0, 0], [1000, 0], [1000, 1000], [0, 1000]]
    data = {"Shell": shell, "TopElevationMm": 0, "ThicknessMm": 200, "CoverMm": 35, "Bars": []}
    meta = {"pathEdges": []}
    def line(a, b):
        return {"kind": "Line", "start": a, "end": b, "midpoint": list(mul(add(a, b), 0.5))}
    def bar(system, kind, layer, position, curves):
        return {"systemId": system, "systemKind": kind, "layer": layer, "setId": system+100,
                "position": position, "typeId": 1, "modelDiameterMm": 12, "nominalDiameterMm": 12,
                "layout": "MaximumSpacing", "curves": curves}
    for k, (name, z, along_x) in enumerate([("Низ X", -147, True), ("Низ Y", -135, False), ("Верх X", -53, True), ("Верх Y", -65, False)]):
        for i, station in enumerate(range(100, 1000, 200)):
            a, b = ([35, station, z], [965, station, z]) if along_x else ([station, 35, z], [station, 965, z])
            data["Bars"].append(bar(k+1, "Area", name, i, [line(a, b)]))
    for j, (a, b) in enumerate(edges(shell)):
        tangent = mul(sub(b, a), 1/1000)
        inward = (-tangent[1], tangent[0])
        system = 10+j
        meta["pathEdges"].append({"Id": system, "EdgeIndex": j, "SourceStart": [*a, 0], "SourceEnd": [*b, 0],
                                  "Tangent": [*tangent, 0], "Inward": [*inward, 0]})
        for i, station in enumerate(range(100, 1000, 200)):
            web = add(add(a, mul(tangent, station)), mul(inward, 41))
            point = lambda t, z: [*add(web, mul(inward, t)), z]
            curves = [line(point(494, -41), point(36, -41)),
                      {"kind": "Arc", "start": point(36, -41), "midpoint": point(36-36/math.sqrt(2), -77+36/math.sqrt(2)), "end": point(0, -77)},
                      line(point(0, -77), point(0, -123)),
                      {"kind": "Arc", "start": point(0, -123), "midpoint": point(36-36/math.sqrt(2), -123-36/math.sqrt(2)), "end": point(36, -159)},
                      line(point(36, -159), point(494, -159))]
            data["Bars"].append(bar(system, "Path", "П ребро "+str(j+1), i, curves))
    data["PhysicalBars"] = len(data["Bars"])
    data["Sets"] = len({b["setId"] for b in data["Bars"]})
    return data, meta


class ExportTests(unittest.TestCase):
    def test_complete_geometry(self):
        d, m = fixture()
        self.assertEqual(audit_geometry(d, m, expected_edges=4)["status"], "PASS")

    def test_missing_edge_is_failure(self):
        d, m = fixture()
        d["Bars"] = [b for b in d["Bars"] if b["systemId"] != 13]
        self.assertEqual(audit_geometry(d, m, expected_edges=4)["status"], "FAIL")

    def test_wrong_z_is_failure(self):
        d, m = fixture()
        for key in ("start", "end", "midpoint"):
            d["Bars"][0]["curves"][0][key][2] = -159
        self.assertEqual(audit_geometry(d, m, expected_edges=4)["status"], "FAIL")

    def test_cover_deficit_not_hidden_by_tolerance(self):
        d, m = fixture()
        d["Bars"][0]["curves"][0]["start"][0] = 34.99
        c = d["Bars"][0]["curves"][0]
        c["midpoint"] = list(mul(add(c["start"], c["end"]), .5))
        self.assertEqual(audit_geometry(d, m, expected_edges=4)["status"], "FAIL")

    def test_missing_middle_position_exceeds_spacing(self):
        d, m = fixture()
        d["Bars"] = [b for b in d["Bars"] if not (b["systemId"] == 10 and b["position"] == 2)]
        d["PhysicalBars"] = len(d["Bars"])
        self.assertEqual(audit_geometry(d, m, expected_edges=4)["status"], "FAIL")

    def test_curve_order_and_direction_do_not_change_matching(self):
        d, _ = fixture()
        a = d["Bars"][-1]
        b = json.loads(json.dumps(a))
        b["curves"].reverse()
        for c in b["curves"]:
            c["start"], c["end"] = c["end"], c["start"]
        self.assertTrue(same_bar(a, b))
        b["curves"][0]["midpoint"][0] += .01
        self.assertFalse(same_bar(a, b))

    def test_duplicate_does_not_depend_on_id_or_direction(self):
        d, _ = fixture()
        a = d["Bars"][-1]
        b = json.loads(json.dumps(a))
        b["systemId"] += 99
        b["setId"] += 99
        for c in b["curves"]:
            c["start"], c["end"] = c["end"], c["start"]
        self.assertEqual(len(duplicate_bars([a, b])), 1)

def compare_footprint(data, footprint):
    errors = []
    actual, expected = data["Shell"], footprint["shell"]
    if len(actual) != len(expected) or any(norm(sub(vec(a, 2), vec(b, 2))) > MATCH_EPS for a, b in zip(actual, expected)):
        errors.append("original ordered shell changed")
    ah, eh = data.get("Holes", []), footprint.get("holes", [])
    if len(ah) != len(eh) or any(len(a) != len(b) or any(norm(sub(vec(p, 2), vec(q, 2))) > MATCH_EPS for p, q in zip(a, b)) for a, b in zip(ah, eh)):
        errors.append("original holes changed")
    for exported, original in (("TopElevationMm", "topElevationMm"), ("ThicknessMm", "thicknessMm")):
        if abs(float(data[exported])-float(footprint[original])) > MATCH_EPS:
            errors.append(exported+" changed")
    return {"status": "FAIL" if errors else "PASS", "errors": errors}


def compare_reopen(before, after):
    errors = []
    a = {bar_identity(b): b for b in before["Bars"]}
    b = {bar_identity(x): x for x in after["Bars"]}
    if len(a) != len(before["Bars"]) or len(b) != len(after["Bars"]): errors.append("duplicate physical-position identity")
    if set(a) != set(b): errors.append("physical-position identities changed")
    changed = []
    for key in a.keys() & b.keys():
        x, y = a[key], b[key]
        if any(x.get(k) != y.get(k) for k in ("typeId", "systemKind", "layer", "layout")) or not same_bar(x, y): changed.append(list(key))
    if changed: errors.append("physical geometry, type, system or layout changed")
    host = compare_footprint(after, {"shell": before["Shell"], "holes": before.get("Holes", []),
                                   "topElevationMm": before["TopElevationMm"], "thicknessMm": before["ThicknessMm"]})
    errors.extend(host["errors"])
    return {"status": "FAIL" if errors else "PASS", "errors": errors, "changedBars": changed,
            "beforeCount": len(before["Bars"]), "afterCount": len(after["Bars"]),
            "exportedSignaturesEqual": before.get("Signature") == after.get("Signature"),
            "comparisonToleranceMm": MATCH_EPS}


class EvidenceTests(unittest.TestCase):
    def test_reopen_full_geometry_order_independent(self):
        a, _ = fixture()
        b = json.loads(json.dumps(a))
        b["Bars"].reverse()
        for bar in b["Bars"]:
            bar["curves"].reverse()
            for c in bar["curves"]:
                c["start"], c["end"] = c["end"], c["start"]
        self.assertEqual(compare_reopen(a, b)["status"], "PASS")

    def test_reopen_missing_bar_and_small_real_shift_fail(self):
        a, _ = fixture()
        b = json.loads(json.dumps(a))
        b["Bars"].pop()
        self.assertEqual(compare_reopen(a, b)["status"], "FAIL")
        b = json.loads(json.dumps(a))
        b["Bars"][0]["curves"][0]["end"][0] += .00002
        self.assertEqual(compare_reopen(a, b)["status"], "FAIL")

    def test_original_footprint_preserved(self):
        a, _ = fixture()
        f = {"shell": a["Shell"], "holes": [], "topElevationMm": 0, "thicknessMm": 200}
        self.assertEqual(compare_footprint(a, f)["status"], "PASS")
        a = json.loads(json.dumps(a))
        a["Shell"][0][0] += .01
        self.assertEqual(compare_footprint(a, f)["status"], "FAIL")

class GeometryTests(unittest.TestCase):
    square = [(0, 0), (10, 0), (10, 10), (0, 10)]

    def test_winding_and_boundary(self):
        self.assertTrue(point_in_ring((5, 5), self.square))
        self.assertTrue(point_in_ring((0, 5), self.square))
        self.assertFalse(point_in_ring((-1, 5), self.square))
        self.assertTrue(point_in_ring((5, 5), list(reversed(self.square))))

    def test_parallel_distance(self):
        self.assertAlmostEqual(segment_distance((0, 0), (10, 0), (2, 3), (8, 3)), 3)

    def test_crossing_and_collinear(self):
        self.assertEqual(segment_distance((0, 0), (10, 10), (0, 10), (10, 0)), 0)
        self.assertEqual(segment_distance((0, 0), (10, 0), (4, 0), (14, 0)), 0)
        self.assertAlmostEqual(segment_distance((0, 0), (2, 0), (4, 0), (14, 0)), 2)

    def test_degenerate_projection(self):
        self.assertAlmostEqual(segment_distance((2, 2), (2, 2), (0, 0), (4, 0)), 2)

    def test_concave_exit_with_both_endpoints_inside(self):
        ring = [(0, 0), (10, 0), (10, 10), (7, 10), (7, 3), (3, 3), (3, 10), (0, 10)]
        self.assertTrue(point_in_ring((1, 8), ring))
        self.assertTrue(point_in_ring((9, 8), ring))
        self.assertEqual(segment_clearance((1, 8), (9, 8), ring), 0)
        self.assertAlmostEqual(segment_clearance((1, 1), (9, 1), ring), 1)
        self.assertLess(segment_clearance((4, 8), (6, 8), ring), 0)

    def test_hole_crossing(self):
        hole = [(4, 4), (6, 4), (6, 6), (4, 6)]
        self.assertEqual(segment_clearance((2, 5), (8, 5), self.square, [hole]), 0)
        self.assertLess(segment_clearance((5, 5), (5.5, 5), self.square, [hole]), 0)

    def test_diagonal_boundary(self):
        diamond = [(-10, 0), (0, -10), (10, 0), (0, 10)]
        self.assertAlmostEqual(segment_clearance((-1, 0), (1, 0), diamond), 9 / math.sqrt(2))

    def test_flat_ends_are_not_capsules(self):
        self.assertAlmostEqual(straight_side_cover((1, 5, 0), (9, 5, 0), 1, self.square), 1)
        self.assertAlmostEqual(straight_side_cover((1, 1, 0), (9, 1, 0), 0.25, self.square), 0.75)

    def test_actual_tooth_slope_reduces_flat_end_cover(self):
        slope = 10/150.001
        cosine = 1/math.sqrt(1+slope*slope)
        ring = [(0, 0), (1000, 0), (1000, 1000), (1000*slope, 1000)]
        a, b = (500*slope+35/cosine, 500, 0), (900, 500, 0)
        cover = straight_side_cover(a, b, 6, ring)
        self.assertAlmostEqual(cover, 35-6*slope*cosine, places=7)
        self.assertLess(cover, 34.61)
    def test_vertical_web_projects_to_disk(self):
        self.assertAlmostEqual(straight_side_cover((2, 5, 0), (2, 5, 8), 1, self.square), 1)

    def test_exact_arc_extrema_and_reverse(self):
        c = {"start": [1, 0, 0], "midpoint": [-math.sqrt(0.5), 0, math.sqrt(0.5)], "end": [0, 0, -1]}
        for c in [c, dict(c, start=c["end"], end=c["start"])]:
            g = arc_geometry(c, 0.2)
            self.assertAlmostEqual(min(p[0] for p in g["xy"]), -1)
            self.assertAlmostEqual(max(p[0] for p in g["xy"]), 1)
            self.assertAlmostEqual(g["z"][0], -1.2)
            self.assertAlmostEqual(g["z"][1], 1.2)

    def test_nonvertical_arc_is_unknown(self):
        with self.assertRaises(ValueError):
            arc_geometry({"start": [1, 0, 0], "midpoint": [0, 1, 0], "end": [-1, 0, 0]})

    def test_invalid_data_is_unknown(self):
        with self.assertRaises(ValueError):
            arc_geometry({"start": [float("nan"), 0, 0], "midpoint": [0, 1, 0], "end": [-1, 0, 0]})










def read_json(path):
    def invalid(value): raise ValueError("JSON nonfinite constant "+value)
    return json.loads(Path(path).read_text(encoding="utf-8-sig"), parse_constant=invalid)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("geometry", nargs="?", help="geometry-before.json")
    parser.add_argument("--after", help="geometry-after.json")
    parser.add_argument("--results", help="results.json containing pathEdges")
    parser.add_argument("--footprint", help="original approved footprint.json")
    parser.add_argument("--cover-mm", type=float, default=35.0)
    parser.add_argument("--max-spacing-mm", type=float, default=200.0)
    parser.add_argument("--expected-edges", type=int, default=14)
    parser.add_argument("--output", help="optional explicit output JSON path")
    parser.add_argument("--self-test", action="store_true")
    args = parser.parse_args()
    if args.self_test:
        tests = unittest.defaultTestLoader.loadTestsFromModule(sys.modules[__name__])
        return 0 if unittest.TextTestRunner(verbosity=2).run(tests).wasSuccessful() else 1
    if not args.geometry or not args.results:
        parser.error("geometry and --results are required unless --self-test")
    try:
        data, meta = read_json(args.geometry), read_json(args.results)
        result = {"before": audit_geometry(data, meta, args.expected_edges, args.max_spacing_mm, args.cover_mm)}
        if args.after:
            after = read_json(args.after)
            result["after"] = audit_geometry(after, meta, args.expected_edges, args.max_spacing_mm, args.cover_mm)
            result["saveReopen"] = compare_reopen(data, after)
        else:
            result["saveReopen"] = {"status": "UNKNOWN", "reason": "No after export supplied"}
        if args.footprint:
            result["originalFootprint"] = compare_footprint(data, read_json(args.footprint))
        else:
            result["originalFootprint"] = {"status": "UNKNOWN", "reason": "No original footprint supplied"}
        statuses = [v["status"] for v in result.values()]
        result["status"] = "FAIL" if "FAIL" in statuses else "UNKNOWN" if "UNKNOWN" in statuses else "PASS"
    except (KeyError, TypeError, ValueError, OSError, ZeroDivisionError) as error:
        result = {"status": "UNKNOWN", "error": str(error)}
    result["method"] = {"dependencies": "Python standard library only; no NTS/Shapely", "units": "mm",
                        "coverNumericalAllowanceMm": COVER_EPS, "geometryComparisonToleranceMm": MATCH_EPS,
                        "straightSide": "exact XY rectangle (horizontal flat-ended cylinders) or disk (vertical cylinders)",
                        "arcSide": "analytic full XY-axis projection plus radius, conservative; inconclusive bound => UNKNOWN",
                        "arcZ": "analytic circular sweep extrema including physical section; no tessellation error",
                        "ignored": "inter-bar collisions, structural capacity, normative anchorage and lap design",
                        "scope": "four straight Area layers at -147/-135/-53/-65 mm; U at -159/-41 mm; nominal/model diameter12"}
    paths = [args.geometry, args.results, args.after, args.footprint]
    result["inputs"] = [{"path": str(Path(p).resolve()), "sha256": hashlib.sha256(Path(p).read_bytes()).hexdigest()} for p in paths if p and Path(p).is_file()]
    text = json.dumps(result, ensure_ascii=False, indent=2, allow_nan=False)
    if args.output:
        Path(args.output).write_text(text+"\n", encoding="utf-8")
    print(text)
    return 0 if result["status"] == "PASS" else 1 if result["status"] == "FAIL" else 2


if __name__ == "__main__":
    sys.exit(main())
