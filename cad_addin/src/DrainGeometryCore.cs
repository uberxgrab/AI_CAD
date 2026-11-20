using System;
using System.Collections.Generic;
using HvacAiCadAddin.Ducts;

namespace HvacAiCadAddin.Pipes
{
    // AutoCAD-free 45 degree joints of HVACDRAIN. On the tree of DrainNetwork (flow toward the root):
    //  - A 90 degree bend is chamfered: the corner is cut by L cos 45 on both legs and a 45 degree piece of
    //    length L joins the cuts (L is the slant length, 300 mm by default, so the cut is 212.13 mm).
    //  - A right-angle T is made with a slant: the branch stops L cos 45 short of the main's centreline and a
    //    45 degree piece of length L runs to the main, landing L cos 45 DOWNSTREAM (toward the root) of the
    //    original T point. The slant belongs to the branch; the main stays straight, and the junction moves
    //    to the landing point.
    //  - Where the two upstream pipes are in line with each other and the pipe toward the root leaves at
    //    right angles (a T whose stem is the downstream pipe), both upstream pipes get a slant and meet on
    //    the stem L cos 45 downstream of the original joint (a Y).
    //  - If a leg is too short for the cuts it has to take (counting the neighbouring chamfer or slant on
    //    the same leg), or the landing would pass the next node, the corner or T is kept as drawn and
    //    reported. A joint of four or more pipes is not handled and stays a plain cross.
    // See AGENTS.md, "HVACDRAIN: draw and size a condensate network".

    public sealed class DrainGeometryResult
    {
        /// <summary>The finished pipe of each segment (by <see cref="DrainSegment.Index"/>), upstream to downstream.</summary>
        public readonly List<List<Vec2>> Segments = new List<List<Vec2>>();

        /// <summary>Corners and T joints kept as drawn, and joints that are not right angles.</summary>
        public readonly List<string> Warnings = new List<string>();
        public readonly List<Vec2> WarningPoints = new List<Vec2>();

        /// <summary>Bends chamfered.</summary>
        public int Chamfers;

        /// <summary>T joints (and Y joints) made with 45 degree slants.</summary>
        public int Slants;

        /// <summary>Corners and joints kept as drawn because a leg was too short.</summary>
        public int Kept;

        /// <summary>The distance from a corner to where its 45 degree piece starts (mm).</summary>
        public double Cut;
    }

    public static class DrainGeometry
    {
        /// <summary>How far from 90 degrees a bend or T may be and still count as a right angle.</summary>
        public const double RightAngleToleranceDegrees = 5;

        private const double Eps = 1e-6;

        private enum FeatureKind
        {
            Bend,
            Tee,
            Wye
        }

        private struct Use
        {
            public int Segment;
            public int Leg;
            public bool AtStart;
        }

        private sealed class Feature
        {
            public FeatureKind Kind;
            public Vec2 At;
            public bool Enabled = true;
            public string Reason;

            // A bend: the segment and the interior point.
            public int Segment;
            public int Index;
            public Vec2 CutBack;
            public Vec2 CutForward;

            // A junction: the main (Tee only), where the branches stop and where they land.
            public int Main = -1;
            public Vec2 Landing;
            public readonly Dictionary<int, Vec2> BranchStop = new Dictionary<int, Vec2>();

            public readonly List<Use> Uses = new List<Use>();
        }

        public static DrainGeometryResult Build(DrainTree tree, double slantLength)
        {
            DrainGeometryResult result = new DrainGeometryResult();
            double cut = slantLength * Math.Cos(Math.PI / 4);
            result.Cut = cut;

            List<Feature> features = new List<Feature>();
            Dictionary<long, Feature> bends = new Dictionary<long, Feature>();
            Dictionary<int, Feature> junctions = new Dictionary<int, Feature>();
            double tolerance = RightAngleToleranceDegrees * Math.PI / 180.0;

            // 1. The bends that are right angles.
            foreach (DrainSegment segment in tree.Segments)
            {
                IList<Vec2> p = segment.Points;
                for (int i = 1; i + 1 < p.Count; i++)
                {
                    Vec2 back = (p[i - 1] - p[i]).Normalized();
                    Vec2 forward = (p[i + 1] - p[i]).Normalized();
                    double angle = Math.Acos(Math.Max(-1.0, Math.Min(1.0, back.Dot(forward))));
                    if (Math.Abs(angle - Math.PI / 2) > tolerance)
                        continue;

                    Feature bend = new Feature
                    {
                        Kind = FeatureKind.Bend,
                        At = p[i],
                        Segment = segment.Index,
                        Index = i,
                        CutBack = p[i] + back * cut,
                        CutForward = p[i] + forward * cut
                    };
                    bend.Uses.Add(new Use { Segment = segment.Index, Leg = i - 1, AtStart = false });
                    bend.Uses.Add(new Use { Segment = segment.Index, Leg = i, AtStart = true });
                    features.Add(bend);
                    bends[BendKey(segment.Index, i)] = bend;
                }
            }

            // 2. The T junctions.
            foreach (DrainNode node in tree.Nodes)
            {
                if (node.Kind != DrainNodeKind.Junction || node.UpSegments.Count != 2 || node.DownSegment < 0)
                    continue;

                Feature joint = PlanJunction(tree, node, cut, tolerance, result);
                if (joint == null)
                    continue;
                features.Add(joint);
                junctions[node.Index] = joint;
            }

            // 3. Give up the features whose legs are too short, one at a time, until every leg fits.
            ResolveShortLegs(tree, features, cut);
            foreach (Feature feature in features)
            {
                if (feature.Enabled)
                    continue;
                result.Kept++;
                result.Warnings.Add(feature.Reason);
                result.WarningPoints.Add(feature.At);
            }

            // 4. The finished pipes.
            foreach (DrainSegment segment in tree.Segments)
            {
                IList<Vec2> p = segment.Points;
                List<Vec2> list = new List<Vec2>();

                DrainNode up = tree.Nodes[segment.UpNode];
                Feature upJoint;
                if (up.Kind == DrainNodeKind.Junction && junctions.TryGetValue(up.Index, out upJoint) && upJoint.Enabled)
                    Add(list, upJoint.Landing);
                else
                    Add(list, p[0]);

                for (int i = 1; i + 1 < p.Count; i++)
                {
                    Feature bend;
                    if (bends.TryGetValue(BendKey(segment.Index, i), out bend) && bend.Enabled)
                    {
                        Add(list, bend.CutBack);
                        Add(list, bend.CutForward);
                        result.Chamfers++;
                    }
                    else
                    {
                        Add(list, p[i]);
                    }
                }

                DrainNode down = tree.Nodes[segment.DownNode];
                Feature downJoint;
                if (down.Kind == DrainNodeKind.Junction && junctions.TryGetValue(down.Index, out downJoint) && downJoint.Enabled)
                {
                    if (downJoint.Kind == FeatureKind.Tee && downJoint.Main == segment.Index)
                    {
                        Add(list, downJoint.Landing);
                    }
                    else
                    {
                        Add(list, downJoint.BranchStop[segment.Index]);
                        Add(list, downJoint.Landing);
                    }
                }
                else
                {
                    Add(list, p[p.Count - 1]);
                }

                result.Segments.Add(list);
            }

            foreach (Feature joint in junctions.Values)
            {
                if (joint.Enabled)
                    result.Slants += joint.BranchStop.Count;
            }

            return result;
        }

        private static long BendKey(int segment, int index)
        {
            return ((long)segment << 32) | (uint)index;
        }

        private static void Add(List<Vec2> list, Vec2 point)
        {
            if (list.Count > 0 && list[list.Count - 1].DistanceTo(point) < Eps)
                return;
            list.Add(point);
        }

        // Works out how a three-pipe joint is made, or null when it is left as drawn (a warning is added when
        // that is not simply an existing 45 degree joint).
        private static Feature PlanJunction(
            DrainTree tree, DrainNode node, double cut, double tolerance, DrainGeometryResult result)
        {
            DrainSegment down = tree.Segments[node.DownSegment];
            Vec2 j = node.Point;
            Vec2 d = (down.Points[1] - down.Points[0]).Normalized();

            int[] ups = { node.UpSegments[0], node.UpSegments[1] };
            Vec2[] u = new Vec2[2];
            for (int k = 0; k < 2; k++)
            {
                IList<Vec2> p = tree.Segments[ups[k]].Points;
                u[k] = (p[p.Count - 2] - p[p.Count - 1]).Normalized();
            }

            double cosTolerance = Math.Cos(tolerance);
            double sinTolerance = Math.Sin(tolerance);
            int main = -1;
            int branch = -1;
            bool wye = false;
            if (d.Dot(u[0]) <= -cosTolerance && Math.Abs(d.Dot(u[1])) <= sinTolerance)
            {
                main = 0;
                branch = 1;
            }
            else if (d.Dot(u[1]) <= -cosTolerance && Math.Abs(d.Dot(u[0])) <= sinTolerance)
            {
                main = 1;
                branch = 0;
            }
            else if (u[0].Dot(u[1]) <= -cosTolerance
                && Math.Abs(d.Dot(u[0])) <= sinTolerance && Math.Abs(d.Dot(u[1])) <= sinTolerance)
            {
                wye = true;
            }
            else
            {
                // Not a right-angle T. A branch that already leaves the main at 45 degrees (a Y) is left alone.
                bool alreadyDiagonal = false;
                for (int k = 0; k < 2; k++)
                {
                    int other = 1 - k;
                    if (d.Dot(u[k]) <= -cosTolerance
                        && Math.Abs(Math.Acos(Math.Max(-1.0, Math.Min(1.0, d.Dot(u[other])))) - 3 * Math.PI / 4) <= tolerance)
                        alreadyDiagonal = true;
                }

                // Two pipes that already meet the downstream pipe at 45 degrees each (the Y a slant joint makes).
                double first = Math.Acos(Math.Max(-1.0, Math.Min(1.0, d.Dot(u[0]))));
                double second = Math.Acos(Math.Max(-1.0, Math.Min(1.0, d.Dot(u[1]))));
                if (Math.Abs(first - 3 * Math.PI / 4) <= tolerance && Math.Abs(second - 3 * Math.PI / 4) <= tolerance)
                    alreadyDiagonal = true;

                if (!alreadyDiagonal)
                {
                    result.Warnings.Add(
                        "The joint at " + DrainNetwork.At(j) + " is not a right-angle T: it is left as drawn.");
                    result.WarningPoints.Add(j);
                }

                return null;
            }

            Feature feature = new Feature
            {
                Kind = wye ? FeatureKind.Wye : FeatureKind.Tee,
                At = j,
                Landing = j + d * cut
            };
            feature.Uses.Add(new Use { Segment = down.Index, Leg = 0, AtStart = true });
            if (wye)
            {
                for (int k = 0; k < 2; k++)
                {
                    feature.BranchStop[ups[k]] = j + u[k] * cut;
                    feature.Uses.Add(new Use
                    {
                        Segment = ups[k],
                        Leg = tree.Segments[ups[k]].Points.Count - 2,
                        AtStart = false
                    });
                }
            }
            else
            {
                feature.Main = ups[main];
                feature.BranchStop[ups[branch]] = j + u[branch] * cut;
                feature.Uses.Add(new Use
                {
                    Segment = ups[branch],
                    Leg = tree.Segments[ups[branch]].Points.Count - 2,
                    AtStart = false
                });
            }

            return feature;
        }

        // Every enabled feature takes a cut from the legs it touches. A leg whose two cuts add up to more
        // than its length gives up one feature: the one at its start, or the one at its end when the start
        // has none. The cuts are then added up again, since giving up a feature frees its other legs.
        private static void ResolveShortLegs(DrainTree tree, List<Feature> features, double cut)
        {
            while (true)
            {
                int count = tree.Segments.Count;
                double[][] atStart = new double[count][];
                double[][] atEnd = new double[count][];
                Feature[][] startFeature = new Feature[count][];
                Feature[][] endFeature = new Feature[count][];
                for (int s = 0; s < count; s++)
                {
                    int legs = Math.Max(0, tree.Segments[s].Points.Count - 1);
                    atStart[s] = new double[legs];
                    atEnd[s] = new double[legs];
                    startFeature[s] = new Feature[legs];
                    endFeature[s] = new Feature[legs];
                }

                foreach (Feature feature in features)
                {
                    if (!feature.Enabled)
                        continue;
                    foreach (Use use in feature.Uses)
                    {
                        if (use.AtStart)
                        {
                            atStart[use.Segment][use.Leg] += cut;
                            startFeature[use.Segment][use.Leg] = feature;
                        }
                        else
                        {
                            atEnd[use.Segment][use.Leg] += cut;
                            endFeature[use.Segment][use.Leg] = feature;
                        }
                    }
                }

                bool changed = false;
                for (int s = 0; s < count && !changed; s++)
                {
                    IList<Vec2> p = tree.Segments[s].Points;
                    for (int k = 0; k + 1 < p.Count; k++)
                    {
                        double length = p[k].DistanceTo(p[k + 1]);
                        double needed = atStart[s][k] + atEnd[s][k];
                        if (needed <= length + Eps)
                            continue;

                        Feature give = startFeature[s][k] ?? endFeature[s][k];
                        give.Enabled = false;
                        give.Reason = Describe(give) + " is kept as drawn: a leg of "
                            + DrainNetwork.Num(length) + " mm is shorter than the "
                            + DrainNetwork.Num(needed) + " mm the 45 degree cuts need.";
                        changed = true;
                        break;
                    }
                }

                if (!changed)
                    return;
            }
        }

        private static string Describe(Feature feature)
        {
            string what = feature.Kind == FeatureKind.Bend ? "The corner" : "The joint";
            return what + " at " + DrainNetwork.At(feature.At);
        }
    }
}
