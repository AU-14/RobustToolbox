using Robust.Shared.Physics.Collision.Shapes;

namespace Robust.Shared.Physics.Collision;

internal sealed partial class CollisionManager
{
    /// <summary>
    /// Test overlap between the two shapes.
    /// </summary>
    /// <param name="shapeA">The first shape.</param>
    /// <param name="shapeB">The second shape.</param>
    /// <param name="xfA">The transform for the first shape.</param>
    /// <param name="xfB">The transform for the seconds shape.</param>
    /// <returns></returns>
    public bool TestOverlap<T, U>(
        T shapeA,
        int indexA,
        U shapeB,
        int indexB,
        in Transform xfA,
        in Transform xfB,
        bool ignoreShapeSkin = false)
        where T : IPhysShape
        where U : IPhysShape
    {
        // CMU14: circle sensors have one support point each, so no simplex search is needed.
        if (shapeA is PhysShapeCircle circleA && shapeB is PhysShapeCircle circleB)
        {
            var pointA = Transform.Mul(xfA, circleA.Position);
            var pointB = Transform.Mul(xfB, circleB.Position);
            var distance = (pointA - pointB).Length();

            // Keep DistanceManager's subtraction order and tolerance at the touching boundary.
            // ignoreShapeSkin preserves circle radii, including in the generic path below.
            return distance < float.Epsilon ||
                   System.MathF.Max(0.0f, distance - circleA.Radius - circleB.Radius) < 10.0f * float.Epsilon;
        }

        var input = new DistanceInput();

        input.ProxyA.Set(in shapeA, indexA);
        input.ProxyB.Set(in shapeB, indexB);

        if (ignoreShapeSkin)
        {
            if (shapeA.ShapeType != ShapeType.Circle)
                input.ProxyA.Radius = 0f;

            if (shapeB.ShapeType != ShapeType.Circle)
                input.ProxyB.Radius = 0f;
        }

        input.TransformA = xfA;
        input.TransformB = xfB;
        input.UseRadii = true;

        DistanceManager.ComputeDistance(out var output, out _, input);

        return output.Distance < 10.0f * float.Epsilon;
    }
}
