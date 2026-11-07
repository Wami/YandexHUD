#!/usr/bin/env python3
"""Generate a 1 Hz mock-GPS route from OSRM.

Example:
  python3 make_route.py --from 55.748519,38.003984 --to 55.735,38.030 \
      --speed-kmh 60 --out route.json
"""
import argparse
import json
import math
import urllib.parse
import urllib.request

EARTH = 6371008.8


def haversine(a, b):
    lat1, lon1 = map(math.radians, a)
    lat2, lon2 = map(math.radians, b)
    dlat = lat2 - lat1
    dlon = lon2 - lon1
    h = math.sin(dlat / 2) ** 2 + math.cos(lat1) * math.cos(lat2) * math.sin(dlon / 2) ** 2
    return 2 * EARTH * math.asin(math.sqrt(h))


def bearing(a, b):
    lat1, lon1 = map(math.radians, a)
    lat2, lon2 = map(math.radians, b)
    dlon = lon2 - lon1
    y = math.sin(dlon) * math.cos(lat2)
    x = math.cos(lat1) * math.sin(lat2) - math.sin(lat1) * math.cos(lat2) * math.cos(dlon)
    return (math.degrees(math.atan2(y, x)) + 360) % 360


def interpolate(a, b, fraction):
    return (a[0] + (b[0] - a[0]) * fraction, a[1] + (b[1] - a[1]) * fraction)


def fetch_route(start, end):
    coords = f"{start[1]},{start[0]};{end[1]},{end[0]}"
    query = urllib.parse.urlencode({
        "overview": "full",
        "geometries": "geojson",
        "steps": "true",
    })
    url = f"https://router.project-osrm.org/route/v1/driving/{coords}?{query}"
    with urllib.request.urlopen(url, timeout=30) as response:
        data = json.load(response)
    if data.get("code") != "Ok" or not data.get("routes"):
        raise RuntimeError(f"OSRM error: {data.get('code')}")
    route = data["routes"][0]
    geometry = [(lat, lon) for lon, lat in route["geometry"]["coordinates"]]
    return geometry, route["distance"], route["duration"]


def shift(point, meters, direction):
    lat = point[0] + (meters * math.cos(math.radians(direction))) / 111320.0
    lon = point[1] + (meters * math.sin(math.radians(direction))) / (
        111320.0 * math.cos(math.radians(point[0])))
    return (lat, lon)


def build_points(geometry, speed_mps, detour):
    cumulative = [0.0]
    for a, b in zip(geometry, geometry[1:]):
        cumulative.append(cumulative[-1] + haversine(a, b))
    total = cumulative[-1]
    if total <= 0:
        raise RuntimeError("empty route")

    if detour:
        detour_start = total * 0.30
        detour_end = total * 0.38
    else:
        detour_start = detour_end = -1.0

    points = []
    distance = 0.0
    index = 0
    while distance <= total:
        while index + 1 < len(geometry) - 1 and cumulative[index + 1] < distance:
            index += 1
        segment_start = cumulative[index]
        segment_end = cumulative[index + 1] if index + 1 < len(cumulative) else cumulative[index]
        segment_length = max(segment_end - segment_start, 1e-9)
        fraction = min(max((distance - segment_start) / segment_length, 0.0), 1.0)
        point = interpolate(geometry[index], geometry[index + 1], fraction)
        course = bearing(geometry[index], geometry[index + 1])
        if detour_start <= distance <= detour_end:
            point = shift(point, detour, course + 90)
        points.append({
            "lat": round(point[0], 7),
            "lon": round(point[1], 7),
            "speed": round(speed_mps, 3),
            "bearing": round(course, 1),
        })
        distance += speed_mps
    return points, total


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--from", dest="start", default="55.748519,38.003984")
    parser.add_argument("--to", dest="end", default="55.735000,38.030000")
    parser.add_argument("--speed-kmh", type=float, default=60.0)
    parser.add_argument("--detour", type=float, default=0.0,
                        help="offset in meters for a 30-38%% segment")
    parser.add_argument("--out", default="route.json")
    args = parser.parse_args()
    start = tuple(map(float, args.start.split(",")))
    end = tuple(map(float, args.end.split(",")))
    if not (len(start) == len(end) == 2):
        parser.error("coordinates must be lat,lon")
    geometry, distance, duration = fetch_route(start, end)
    points, total = build_points(geometry, args.speed_kmh / 3.6, args.detour)
    payload = {
        "name": f"{args.start} -> {args.end} ({total / 1000:.1f} км, "
                f"{args.speed_kmh:.0f} км/ч, OSRM {duration:.0f} с)",
        "points": points,
    }
    with open(args.out, "w", encoding="utf-8") as handle:
        json.dump(payload, handle, ensure_ascii=False, separators=(",", ":"))
    print(f"{args.out}: {total / 1000:.2f} km, {len(points)} points, "
          f"{len(points)} s at {args.speed_kmh:.0f} km/h")


if __name__ == "__main__":
    main()
