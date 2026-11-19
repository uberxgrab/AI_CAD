using HvacAiCadAddin.Ducts;

namespace DuctCoreTests
{
    /// <summary>Where a picked point lies on a duct: end faces and side walls, rotated and mirrored.</summary>
    internal static class HitTests
    {
        public static void Run()
        {
            Horizontal();
            Rotated();
            Mirrored();
        }

        private static void Expect(
            string name, DuctGeometry duct, double px, double py,
            DuctHitKind kind, double pointX, double pointY, double outX, double outY)
        {
            Check.Case(name, () =>
            {
                DuctHit hit = DuctHitTest.Classify(duct, new Vec2(px, py), 25);
                if (hit == null)
                {
                    Check.Fail(name, kind.ToString(), "no hit");
                    return;
                }

                Check.Equal(name + " kind", kind, hit.Kind);
                Check.Near(name + " point x", pointX, hit.Point.X, 1e-6);
                Check.Near(name + " point y", pointY, hit.Point.Y, 1e-6);
                Check.Near(name + " outward x", outX, hit.Outward.X, 1e-9);
                Check.Near(name + " outward y", outY, hit.Outward.Y, 1e-9);
            });
        }

        private static void Miss(string name, DuctGeometry duct, double px, double py)
        {
            Check.Case(name, () =>
            {
                Check.True(name, DuctHitTest.Classify(duct, new Vec2(px, py), 25) == null);
            });
        }

        private static void Horizontal()
        {
            // Start (0,0), axis +X, 2000 long, 600 wide: walls at y = +-300.
            DuctGeometry duct = new DuctGeometry(new Vec2(0, 0), new Vec2(1, 0), 2000, 600);
            Expect("end face", duct, 2000, 10, DuctHitKind.EndFace, 2000, 0, 1, 0);
            Expect("start face", duct, 0, -50, DuctHitKind.StartFace, 0, 0, -1, 0);
            Expect("left wall", duct, 1000, 295, DuctHitKind.LeftWall, 1000, 300, 0, 1);
            Expect("right wall", duct, 1000, -310, DuctHitKind.RightWall, 1000, -300, 0, -1);
            Expect("corner pick prefers the nearer edge", duct, 2010, 295, DuctHitKind.LeftWall, 2000, 300, 0, 1);
            Miss("centre is not an edge", duct, 1000, 0);
            Miss("far away", duct, 1000, 800);
            Miss("past the end", duct, 2100, 0);
            Miss("beside the end, outside the width", duct, 2000, 340);
        }

        private static void Rotated()
        {
            // Start (100,100), axis +Y, 1000 long, 600 wide: left of +Y is -X, so the left wall is x = -200.
            DuctGeometry duct = new DuctGeometry(new Vec2(100, 100), new Vec2(0, 1), 1000, 600);
            Expect("rotated left wall", duct, -195, 600, DuctHitKind.LeftWall, -200, 600, -1, 0);
            Expect("rotated right wall", duct, 405, 600, DuctHitKind.RightWall, 400, 600, 1, 0);
            Expect("rotated end face", duct, 100, 1100, DuctHitKind.EndFace, 100, 1100, 0, 1);
            Expect("rotated start face", duct, 150, 95, DuctHitKind.StartFace, 100, 100, 0, -1);
        }

        private static void Mirrored()
        {
            // A mirrored insert: Position is the start, the axis points -X. End is at (3000,0).
            DuctGeometry duct = new DuctGeometry(new Vec2(5000, 0), new Vec2(-1, 0), 2000, 600);
            Check.Near("end x", 3000, duct.End.X);
            Expect("mirrored end face", duct, 3000, 0, DuctHitKind.EndFace, 3000, 0, -1, 0);
            Expect("mirrored start face", duct, 5000, 0, DuctHitKind.StartFace, 5000, 0, 1, 0);
            // Left of -X is -Y.
            Expect("mirrored left wall", duct, 4000, -298, DuctHitKind.LeftWall, 4000, -300, 0, -1);
            Expect("mirrored right wall", duct, 4000, 305, DuctHitKind.RightWall, 4000, 300, 0, 1);
        }
    }
}
