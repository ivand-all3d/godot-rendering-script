using Godot;

public static class Transform3DExt
{
    public static float[][] AsFloatArray(this Transform3D t)
    {
        // The Basis stores the 3×3 rotation‑scale; origin is the translation.
        // Godot’s column accessors (X, Y, Z) already give Vector3 columns.
        var b = t.Basis;

        return new float[][]
        {
            [b.Column0.X, b.Column1.X, b.Column2.X, t.Origin.X],
            [b.Column0.Y, b.Column1.Y, b.Column2.Y, t.Origin.Y],
            [b.Column0.Z, b.Column1.Z, b.Column2.Z, t.Origin.Z],
            [0f,          0f,          0f,          1f]
        };
    }
}