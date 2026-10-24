namespace SlabReinforcement
{
    internal static class CadImportPlacement
    {
        public static CadTransform3D GetEffectiveTransform(CadTransform3D instanceTransform, CadTransform3D totalTransform)
        {
            return totalTransform;
        }

        public static CadPoint3D TransformVertex(CadPoint3D sourceVertex, CadTransform3D effectiveTransform)
        {
            return effectiveTransform.OfPoint(sourceVertex);
        }
    }
}
