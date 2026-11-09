using System.Numerics;
using NUnit.Framework;
using Robust.Shared.Maths;
using Robust.Shared.Physics;

namespace Robust.Shared.Tests.Physics;

// CMU14 class
[TestFixture]
public sealed class CMUFiniteRayTest
{
    [Test]
    public void PreciseBoundsExcludeDistantLeavesWhoseFatBoundsIntersectTheSegment()
    {
        var tree = new DynamicTree<Box2>(static (in Box2 bounds) => bounds);
        var near = new Box2(3, -1, 4, 1);
        var far = new Box2(4, -1, 5, 1);
        tree.Add(near);
        tree.Add(far, new Box2(0, -1, 5, 1));
        var ray = new Ray(Vector2.Zero, Vector2.UnitX);
        var hits = new List<Box2>();
        tree.QueryRay(ref hits, static (ref List<Box2> found, in Box2 box, in Vector2 point, float distance) =>
        {
            found.Add(box);
            return true;
        }, ray, 3);
        Assert.That(hits, Is.EqualTo(new[] { near }));

        hits.Clear();
        tree.QueryRay(ref hits, static (ref List<Box2> found, in Box2 box, in Vector2 point, float distance) =>
        {
            found.Add(box);
            return true;
        }, ray, 3, approx: true);
        Assert.That(hits, Is.EquivalentTo(new[] { near, far }), "Approximate queries must still use their tree bounds.");
    }

    [Test]
    public void BoundedTraversalMatchesFilteredUnboundedOrderAfterMovingLeaves()
    {
        var tree = new B2DynamicTree<int>();
        var moving = tree.CreateProxy(new Box2(2, -0.5f, 3, 0.5f), uint.MaxValue, 0);
        tree.CreateProxy(new Box2(-1, -1, 1, 1), uint.MaxValue, 1);
        tree.CreateProxy(new Box2(3, -0.5f, 4, 0.5f), uint.MaxValue, 2);
        // The near bound must not cause the ray to visit leaves farther along the same line.
        for (var i = 3; i < 256; i++)
            tree.CreateProxy(Box2.UnitCentered.Translated(new Vector2(i * 4, 0)), uint.MaxValue, i);

        void Check()
        {
            foreach (var ray in new[]
                     {
                         new Ray(Vector2.Zero, Vector2.UnitX),
                         new Ray(new Vector2(6, 0), -Vector2.UnitX),
                         new Ray(new Vector2(-2, -2), Vector2.Normalize(Vector2.One)),
                     })
            {
                foreach (var length in new[] { 0f, 3f, float.PositiveInfinity })
                {
                    var expected = new List<(int, float, Vector2)>();
                    tree.RayCast(ref expected,
                        (ref List<(int, float, Vector2)> hits, DynamicTree.Proxy proxy, in Vector2 point, float distance) =>
                        {
                            if (distance <= length)
                                hits.Add((tree.GetUserData(proxy), distance, point));
                            return true;
                        }, ray);

                    var actual = new List<(int, float, Vector2)>();
                    tree.RayCast(ref actual,
                        (ref List<(int, float, Vector2)> hits, DynamicTree.Proxy proxy, in Vector2 point, float distance) =>
                        {
                            hits.Add((tree.GetUserData(proxy), distance, point));
                            return true;
                        }, ray, length);
                    Assert.That(actual, Is.EqualTo(expected), $"Ray {ray.Position}/{ray.Direction}, length {length}");

                    actual.Clear();
                    tree.RayCast(ref actual,
                        (ref List<(int, float, Vector2)> hits, DynamicTree.Proxy proxy, in Vector2 point, float distance) =>
                        {
                            hits.Add((tree.GetUserData(proxy), distance, point));
                            return false;
                        }, ray, length);
                    Assert.That(actual, Is.EqualTo(expected.Take(1)), "Early termination must retain traversal order.");
                }
            }
        }

        Check();
        tree.MoveProxy(moving, new Box2(100, -0.5f, 101, 0.5f));
        tree.Rebuild(true);
        Check();
    }
}
