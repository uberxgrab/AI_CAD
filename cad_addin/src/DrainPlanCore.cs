using System;
using System.Collections.Generic;
using HvacAiCadAddin.Ducts;

namespace HvacAiCadAddin.Pipes
{
    // AutoCAD-free HVACDRAIN pipeline: the rough curves are cleaned by RefrigCleaner (with no root, so no
    // refnets and none of the R1, R2, R3 rules run), the cleaned paths become a tree flowing toward the drain
    // point, and the 45 degree joints are worked out on it. Sizing is separate (DrainSizing) because the dialog
    // redoes it on every edit. See AGENTS.md, "HVACDRAIN: draw and size a condensate network".

    public sealed class DrainPlan
    {
        public bool Ok;
        public string Error;

        /// <summary>Where a failed step found its problem, for a marker.</summary>
        public readonly List<Vec2> ErrorPoints = new List<Vec2>();

        public RefrigResult Clean;
        public DrainTree Tree;
        public DrainGeometryResult Geometry;

        /// <summary>Everything the command reports before the preview: cleaning, tree and joint cases.</summary>
        public readonly List<string> Reports = new List<string>();

        /// <summary>Where to circle those cases.</summary>
        public readonly List<Vec2> ReportPoints = new List<Vec2>();
    }

    public static class DrainPlanner
    {
        /// <param name="curves">The rough LINEs and polylines, as the cleaner takes them.</param>
        /// <param name="baseAngle">The base direction in radians from world X.</param>
        /// <param name="root">A point near the drain end of the network.</param>
        /// <param name="rootTolerance">The root must be a pipe end this close to the picked point.</param>
        public static DrainPlan Run(
            IList<PipeCurve> curves, DrainSettings settings, double baseAngle, Vec2 root, double rootTolerance)
        {
            DrainPlan plan = new DrainPlan();
            string settingsError;
            if (!settings.IsValid(out settingsError))
            {
                plan.Error = settingsError;
                return plan;
            }

            RefrigInput input = new RefrigInput
            {
                Curves = curves,
                Settings = settings.ToRefrigSettings(),
                BaseAngle = baseAngle,
                RootPoint = null
            };
            RefrigResult clean = RefrigCleaner.Run(input);
            plan.Clean = clean;
            if (!clean.Ok)
            {
                plan.Error = clean.Error;
                plan.ErrorPoints.AddRange(clean.ErrorPoints);
                return plan;
            }

            plan.Reports.AddRange(clean.Reports);
            plan.ReportPoints.AddRange(clean.ReportPoints);

            DrainTreeResult built = DrainNetwork.Build(clean.Paths, root, rootTolerance);
            if (!built.Ok)
            {
                plan.Error = built.Error;
                plan.ErrorPoints.AddRange(built.ErrorPoints);
                return plan;
            }

            plan.Tree = built.Tree;
            plan.Reports.AddRange(built.Tree.Reports);
            plan.ReportPoints.AddRange(built.Tree.ReportPoints);

            plan.Geometry = DrainGeometry.Build(built.Tree, settings.SlantLength);
            plan.Reports.AddRange(plan.Geometry.Warnings);
            plan.ReportPoints.AddRange(plan.Geometry.WarningPoints);

            plan.Ok = true;
            return plan;
        }
    }
}
