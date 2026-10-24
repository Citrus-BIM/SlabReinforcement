using Autodesk.Revit.DB;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace SlabReinforcement
{
    public static class ZoneGeometryEngine
    {
        private const double VertexSignatureToleranceFeet = 1.0 / 304.8;
        private const double OrientationBinSizeDegrees = 5.0;
        private const double OrientationCaptureDegrees = 12.5;

        private class EdgeSample
        {
            public XYZ Direction { get; set; }
            public double Length { get; set; }
            public double AngleDegrees { get; set; }
        }

        public static ZoneAnalysisResult AnalyzeFaces(
            IReadOnlyCollection<FiniteElementFace> faces,
            ZoneModelingOptions options)
        {
            List<FiniteElementFace> sourceFaces = faces?.ToList() ?? new List<FiniteElementFace>();
            options ??= new ZoneModelingOptions();
            List<FiniteElementFace> horizontalFaces = sourceFaces
                .Where(face => face.IsHorizontal(options.HorizontalFaceToleranceMm / 304.8))
                .ToList();

            ZoneAnalysisResult result = new ZoneAnalysisResult
            {
                SourceFaces = sourceFaces,
                HorizontalFaces = horizontalFaces,
                LevelsMm = GetRoundedLevelsMm(sourceFaces)
            };

            result.LevelsSummary = FormatLevelSummary(result.LevelsMm);
            result.AngleSummary = BuildAngleSummary(horizontalFaces);

            if (result.SkippedNonHorizontalFaceCount > 0)
            {
                result.Warnings.Add($"Пропущено негоризонтальных граней: {result.SkippedNonHorizontalFaceCount}.");
            }

            if (result.LevelsMm.Count > 1)
            {
                result.Warnings.Add($"Обнаружено несколько уровней Z: {result.LevelsSummary}.");
            }

            if (horizontalFaces.Any(face => face.RebarType == null))
            {
                result.Warnings.Add("Есть грани без назначенного типа арматуры.");
            }

            if (horizontalFaces.Any(face => face.Spacing <= 0))
            {
                result.Warnings.Add("Есть грани с нулевым или отрицательным шагом армирования.");
            }

            if (!horizontalFaces.Any())
            {
                result.Warnings.Add("Не найдено горизонтальных граней для моделирования зон.");
                return result;
            }

            foreach (List<FiniteElementFace> faceGroup in GroupConnectedFaces(
                horizontalFaces,
                options.ElevationGroupingToleranceMm / 304.8))
            {
                LocalCoordinateSystem coordinateSystem = BuildLocalCoordinateSystem(faceGroup);
                List<OrientedFace> orientedFaces = faceGroup
                    .Select(face => new OrientedFace(face, coordinateSystem))
                    .ToList();

                List<OrientedLineData> lines = BuildOrientedLines(
                    orientedFaces,
                    options.AlongLocalX,
                    options.AdjacentElementsToleranceMm / 304.8);

                List<OrientedCluster> clusters = GroupLinesIntoOrientedClusters(
                    lines,
                    options.ZoneMergeToleranceMm / 304.8);

                result.Groups.Add(new ZoneGroupAnalysis
                {
                    Faces = faceGroup,
                    CoordinateSystem = coordinateSystem,
                    DominantAngleDegrees = GetAxisAngleDegrees(coordinateSystem.XAxis),
                    OrientedFaces = orientedFaces,
                    Lines = lines,
                    Clusters = clusters
                });
            }

            if (result.EstimatedZoneCount == 0)
            {
                result.Warnings.Add("После группировки не найдено ни одной рабочей зоны.");
            }

            return result;
        }

        public static string FormatLevelSummary(IReadOnlyCollection<int> levels)
        {
            if (levels == null || levels.Count == 0)
            {
                return "—";
            }

            return string.Join(", ", levels);
        }

        private static List<List<FiniteElementFace>> GroupConnectedFaces(
            List<FiniteElementFace> faces,
            double elevationToleranceFeet)
        {
            List<List<FiniteElementFace>> groupedFaces = new List<List<FiniteElementFace>>();
            if (faces == null || !faces.Any())
            {
                return groupedFaces;
            }

            Dictionary<int, FiniteElementFace> facesById = faces.ToDictionary(face => face.Id);
            Dictionary<int, List<string>> faceVertexKeys = faces.ToDictionary(face => face.Id, GetProjectedVertexKeys);
            Dictionary<string, List<int>> vertexToFaceIds = new Dictionary<string, List<int>>();

            foreach (FiniteElementFace face in faces)
            {
                foreach (string vertexKey in faceVertexKeys[face.Id])
                {
                    if (!vertexToFaceIds.ContainsKey(vertexKey))
                    {
                        vertexToFaceIds[vertexKey] = new List<int>();
                    }

                    vertexToFaceIds[vertexKey].Add(face.Id);
                }
            }

            HashSet<int> visited = new HashSet<int>();
            foreach (FiniteElementFace face in faces.OrderBy(item => item.AverageZ).ThenBy(item => item.Id))
            {
                if (visited.Contains(face.Id))
                {
                    continue;
                }

                Queue<int> queue = new Queue<int>();
                List<FiniteElementFace> group = new List<FiniteElementFace>();
                queue.Enqueue(face.Id);
                visited.Add(face.Id);

                while (queue.Count > 0)
                {
                    int currentId = queue.Dequeue();
                    FiniteElementFace currentFace = facesById[currentId];
                    group.Add(currentFace);

                    foreach (string vertexKey in faceVertexKeys[currentId])
                    {
                        foreach (int neighborId in vertexToFaceIds[vertexKey])
                        {
                            if (visited.Contains(neighborId))
                            {
                                continue;
                            }

                            if (Math.Abs(currentFace.AverageZ - facesById[neighborId].AverageZ) > elevationToleranceFeet)
                            {
                                continue;
                            }

                            visited.Add(neighborId);
                            queue.Enqueue(neighborId);
                        }
                    }
                }

                groupedFaces.Add(group);
            }

            return groupedFaces;
        }

        private static List<OrientedLineData> BuildOrientedLines(
            List<OrientedFace> orientedFaces,
            bool alongLocalX,
            double adjacentToleranceFeet)
        {
            List<OrientedLineData> lines = new List<OrientedLineData>();
            if (!orientedFaces.Any())
            {
                return lines;
            }

            List<List<OrientedFace>> stripes = new List<List<OrientedFace>>();
            List<OrientedFace> currentStripe = null;
            double currentCrossCenter = 0;

            foreach (OrientedFace face in orientedFaces
                .OrderBy(item => GetCrossCenter(item, alongLocalX))
                .ThenBy(item => GetPrimaryMin(item, alongLocalX)))
            {
                double crossCenter = GetCrossCenter(face, alongLocalX);
                if (currentStripe == null)
                {
                    currentStripe = new List<OrientedFace> { face };
                    currentCrossCenter = crossCenter;
                    continue;
                }

                if (Math.Abs(crossCenter - currentCrossCenter) <= adjacentToleranceFeet)
                {
                    currentStripe.Add(face);
                    currentCrossCenter = currentStripe.Average(item => GetCrossCenter(item, alongLocalX));
                }
                else
                {
                    stripes.Add(currentStripe);
                    currentStripe = new List<OrientedFace> { face };
                    currentCrossCenter = crossCenter;
                }
            }

            if (currentStripe != null && currentStripe.Any())
            {
                stripes.Add(currentStripe);
            }

            foreach (List<OrientedFace> stripe in stripes)
            {
                List<OrientedFace> currentSegment = null;
                double currentMaxPrimary = 0;

                foreach (OrientedFace face in stripe
                    .OrderBy(item => GetPrimaryMin(item, alongLocalX))
                    .ThenBy(item => GetCrossCenter(item, alongLocalX)))
                {
                    if (currentSegment == null)
                    {
                        currentSegment = new List<OrientedFace> { face };
                        currentMaxPrimary = GetPrimaryMax(face, alongLocalX);
                        continue;
                    }

                    if (GetPrimaryMin(face, alongLocalX) <= currentMaxPrimary + adjacentToleranceFeet)
                    {
                        currentSegment.Add(face);
                        currentMaxPrimary = Math.Max(currentMaxPrimary, GetPrimaryMax(face, alongLocalX));
                    }
                    else
                    {
                        lines.Add(CreateOrientedLineData(currentSegment, alongLocalX));
                        currentSegment = new List<OrientedFace> { face };
                        currentMaxPrimary = GetPrimaryMax(face, alongLocalX);
                    }
                }

                if (currentSegment != null && currentSegment.Any())
                {
                    lines.Add(CreateOrientedLineData(currentSegment, alongLocalX));
                }
            }

            return lines
                .OrderBy(line => line.MinCross)
                .ThenBy(line => line.MinPrimary)
                .ToList();
        }

        private static OrientedLineData CreateOrientedLineData(List<OrientedFace> faces, bool alongLocalX)
        {
            OrientedFace controllingFace = faces
                .Where(face => face.RebarType != null)
                .OrderByDescending(face => CalculateReinforcementAreaPerMeter(GetBarDiameterFeet(face.RebarType), face.Spacing))
                .FirstOrDefault() ?? faces.First();

            return new OrientedLineData
            {
                RebarType = controllingFace.RebarType,
                Spacing = controllingFace.Spacing,
                MinPrimary = faces.Min(face => GetPrimaryMin(face, alongLocalX)),
                MaxPrimary = faces.Max(face => GetPrimaryMax(face, alongLocalX)),
                MinCross = faces.Min(face => GetCrossMin(face, alongLocalX)),
                MaxCross = faces.Max(face => GetCrossMax(face, alongLocalX)),
                CrossCenter = faces.Average(face => GetCrossCenter(face, alongLocalX)),
                Faces = faces.ToList()
            };
        }

        private static List<OrientedCluster> GroupLinesIntoOrientedClusters(
            List<OrientedLineData> lines,
            double zoneMergeToleranceFeet)
        {
            List<OrientedCluster> clusters = new List<OrientedCluster>();
            OrientedCluster currentCluster = null;

            foreach (OrientedLineData line in lines)
            {
                if (currentCluster == null)
                {
                    currentCluster = CreateCluster(line);
                    clusters.Add(currentCluster);
                    continue;
                }

                OrientedLineData firstLine = currentCluster.Lines.First();
                bool canAdd =
                    Math.Abs(firstLine.MinPrimary - line.MinPrimary) <= zoneMergeToleranceFeet &&
                    Math.Abs(firstLine.MaxPrimary - line.MaxPrimary) <= zoneMergeToleranceFeet &&
                    Math.Abs(firstLine.Spacing - line.Spacing) < 0.001 &&
                    firstLine.RebarType != null &&
                    firstLine.RebarType.Equals(line.RebarType);

                if (canAdd)
                {
                    currentCluster.Lines.Add(line);
                    currentCluster.MinPrimary = Math.Min(currentCluster.MinPrimary, line.MinPrimary);
                    currentCluster.MaxPrimary = Math.Max(currentCluster.MaxPrimary, line.MaxPrimary);
                    currentCluster.MinCross = Math.Min(currentCluster.MinCross, line.MinCross);
                    currentCluster.MaxCross = Math.Max(currentCluster.MaxCross, line.MaxCross);
                }
                else
                {
                    currentCluster = CreateCluster(line);
                    clusters.Add(currentCluster);
                }
            }

            return clusters;
        }

        private static OrientedCluster CreateCluster(OrientedLineData line)
        {
            return new OrientedCluster
            {
                RebarType = line.RebarType,
                Spacing = line.Spacing,
                MinPrimary = line.MinPrimary,
                MaxPrimary = line.MaxPrimary,
                MinCross = line.MinCross,
                MaxCross = line.MaxCross,
                Lines = new List<OrientedLineData> { line }
            };
        }

        private static LocalCoordinateSystem BuildLocalCoordinateSystem(List<FiniteElementFace> faces)
        {
            XYZ origin = new XYZ(
                faces.SelectMany(face => face.Vertices).Average(vertex => vertex.X),
                faces.SelectMany(face => face.Vertices).Average(vertex => vertex.Y),
                0);

            List<EdgeSample> edges = new List<EdgeSample>();
            foreach (FiniteElementFace face in faces)
            {
                for (int i = 0; i < face.Vertices.Count; i++)
                {
                    XYZ start = face.Vertices[i];
                    XYZ end = face.Vertices[(i + 1) % face.Vertices.Count];
                    XYZ direction = new XYZ(end.X - start.X, end.Y - start.Y, 0);
                    double length = direction.GetLength();
                    if (length < VertexSignatureToleranceFeet)
                    {
                        continue;
                    }

                    edges.Add(new EdgeSample
                    {
                        Direction = direction.Normalize(),
                        Length = length,
                        AngleDegrees = GetAxisAngleDegrees(direction)
                    });
                }
            }

            if (!edges.Any())
            {
                return new LocalCoordinateSystem(origin, XYZ.BasisX, XYZ.BasisY);
            }

            int binCount = (int)(180.0 / OrientationBinSizeDegrees);
            double[] bins = new double[binCount];
            foreach (EdgeSample edge in edges)
            {
                int index = (int)Math.Round(edge.AngleDegrees / OrientationBinSizeDegrees) % binCount;
                bins[index] += edge.Length;
            }

            int dominantIndex = Array.IndexOf(bins, bins.Max());
            double dominantDegrees = dominantIndex * OrientationBinSizeDegrees;
            double dominantRadians = dominantDegrees * Math.PI / 180.0;
            XYZ dominantAxis = new XYZ(Math.Cos(dominantRadians), Math.Sin(dominantRadians), 0);

            double sumX = 0;
            double sumY = 0;
            foreach (EdgeSample edge in edges.Where(item => GetAngularDistanceDegrees(item.AngleDegrees, dominantDegrees) <= OrientationCaptureDegrees))
            {
                XYZ direction = edge.Direction;
                if (direction.DotProduct(dominantAxis) < 0)
                {
                    direction = direction.Negate();
                }

                sumX += direction.X * edge.Length;
                sumY += direction.Y * edge.Length;
            }

            XYZ xAxis = new XYZ(sumX, sumY, 0);
            if (xAxis.GetLength() < 1e-9)
            {
                xAxis = dominantAxis;
            }

            return new LocalCoordinateSystem(origin, xAxis, new XYZ(-xAxis.Y, xAxis.X, 0));
        }

        private static string BuildAngleSummary(List<FiniteElementFace> horizontalFaces)
        {
            if (!horizontalFaces.Any())
            {
                return "—";
            }

            LocalCoordinateSystem coordinateSystem = BuildLocalCoordinateSystem(horizontalFaces);
            return GetAxisAngleDegrees(coordinateSystem.XAxis).ToString("0.0°", CultureInfo.InvariantCulture);
        }

        private static List<int> GetRoundedLevelsMm(IEnumerable<FiniteElementFace> faces)
        {
            return faces
                .Select(face => (int)Math.Round(face.AverageZ * 304.8))
                .Distinct()
                .OrderBy(level => level)
                .ToList();
        }

        private static List<string> GetProjectedVertexKeys(FiniteElementFace face)
        {
            return face.Vertices
                .Select(vertex => GetPointKey(vertex, includeZ: false))
                .Distinct()
                .ToList();
        }

        private static string GetPointKey(XYZ point, bool includeZ)
        {
            long x = Quantize(point.X);
            long y = Quantize(point.Y);
            if (!includeZ)
            {
                return $"{x}:{y}";
            }

            long z = Quantize(point.Z);
            return $"{x}:{y}:{z}";
        }

        private static long Quantize(double value)
        {
            return (long)Math.Round(value / VertexSignatureToleranceFeet);
        }

        private static double GetAxisAngleDegrees(XYZ axis)
        {
            double angle = Math.Atan2(axis.Y, axis.X) * 180.0 / Math.PI;
            while (angle < 0)
            {
                angle += 180.0;
            }
            while (angle >= 180.0)
            {
                angle -= 180.0;
            }

            return angle;
        }

        private static double GetAngularDistanceDegrees(double angle1, double angle2)
        {
            double delta = Math.Abs(angle1 - angle2);
            return Math.Min(delta, 180.0 - delta);
        }

        private static double GetPrimaryMin(OrientedFace face, bool alongLocalX) => alongLocalX ? face.MinU : face.MinV;
        private static double GetPrimaryMax(OrientedFace face, bool alongLocalX) => alongLocalX ? face.MaxU : face.MaxV;
        private static double GetCrossMin(OrientedFace face, bool alongLocalX) => alongLocalX ? face.MinV : face.MinU;
        private static double GetCrossMax(OrientedFace face, bool alongLocalX) => alongLocalX ? face.MaxV : face.MaxU;
        private static double GetCrossCenter(OrientedFace face, bool alongLocalX) => alongLocalX ? face.CenterV : face.CenterU;

        private static double GetBarDiameterFeet(Autodesk.Revit.DB.Structure.RebarBarType rebarType)
        {
#if R2019 || R2020 || R2021
            return rebarType.BarDiameter;
#else
            return rebarType.BarNominalDiameter;
#endif
        }

        private static double CalculateReinforcementAreaPerMeter(double diameterFeet, double spacingMillimeters)
        {
            if (spacingMillimeters <= 0)
            {
                return 0;
            }

            double diameterMillimeters = diameterFeet * 304.8;
            double area = Math.PI * Math.Pow(diameterMillimeters, 2) / 4.0;
            return area * (1000.0 / spacingMillimeters);
        }
    }
}
