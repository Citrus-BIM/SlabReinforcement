using System.Text;
using SlabReinforcement.Dxf;
using Xunit;

namespace SlabReinforcement.Core.Tests;

public class DxfSourceTests
{
    private const string Triangle =
        "0\n3DFACE\n5\nFACE_A\n8\ncalc\n" +
        "10\n1200.25\n20\n-2300.5\n30\n17\n" +
        "11\n1210.25\n21\n-2300.5\n31\n17\n" +
        "12\n1200.25\n22\n-2290.5\n32\n17\n";

    [Theory]
    [InlineData("")]
    [InlineData("62\n256\n")]
    public void R12_default_and_explicit_ByLayer_resolve_without_changing_coordinates(string color)
    {
        var source = Read(Document(Triangle + color));
        var face = Assert.Single(source.Faces);

        Assert.Equal("AC1009", source.Version);
        Assert.Null(source.InsUnits);
        Assert.Equal("FACE_A", face.Id);
        Assert.Equal("calc", face.Layer);
        Assert.Equal("ACI:256", face.OriginalColorKey);
        Assert.Equal("ACI:3", face.ColorKey);
        Assert.Equal(3, source.LayerColors["calc"]);
        Assert.Equal(1200.25, face.Vertices[0].X);
        Assert.Equal(-2300.5, face.Vertices[0].Y);
        Assert.Equal(17, face.Vertices[0].Z);
        Assert.Equal(4, face.Vertices.Count);
        Assert.Equal(face.Vertices[2], face.Vertices[3]);
    }

    [Fact]
    public void Model_space_ByBlock_remains_unresolved_instead_of_becoming_black_or_layer_color()
    {
        var source = Read(Document(Triangle + "62\n0\n"));
        var face = Assert.Single(source.Faces);

        Assert.Equal("ACI:0", face.OriginalColorKey);
        Assert.Equal("ACI:0", face.ColorKey);
        Assert.Contains(source.Warnings, warning => warning.Contains("По блоку", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(5)]
    [InlineData(0)]
    [InlineData(256)]
    public void TrueColor_has_priority_over_indexed_ByBlock_and_ByLayer_colors(int aci)
    {
        var face = Assert.Single(Read(Document(Triangle + $"62\n{aci}\n420\n16711808\n")).Faces);

        Assert.Equal("#FF0080", face.ColorKey);
        Assert.Equal("#FF0080", face.OriginalColorKey);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(6)]
    public void Missing_unitless_millimetre_and_metre_metadata_are_preserved_without_scaling(int? units)
    {
        string unitHeader = units.HasValue ? $"9\n$INSUNITS\n70\n{units.Value}\n" : "";
        var source = Read(Document(Triangle, unitHeader));

        Assert.Equal(units, source.InsUnits);
        Assert.Equal(1210.25, Assert.Single(source.Faces).Vertices[1].X);
        Assert.Equal(!units.HasValue || units == 0,
            source.Warnings.Any(warning => warning.Contains("единицы длины", StringComparison.Ordinal)));
    }

    [Fact]
    public void Explicit_fourth_vertex_and_different_elevations_are_retained_for_downstream_selection()
    {
        var source = Read(Document(Triangle + "13\n1210.25\n23\n-2290.5\n33\n549.45\n"));
        var face = Assert.Single(source.Faces);

        Assert.Equal(4, face.Vertices.Count);
        Assert.Equal(17, face.Vertices[0].Z);
        Assert.Equal(549.45, face.Vertices[3].Z);
        Assert.Equal(1210.25, face.Vertices[3].X);
        Assert.Equal(-2290.5, face.Vertices[3].Y);
    }

    [Theory]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("-Infinity")]
    public void Nonfinite_coordinates_are_rejected_before_they_reach_geometry_algorithms(string coordinate)
    {
        string malformed = Triangle.Replace("10\n1200.25\n", "10\n" + coordinate + "\n", StringComparison.Ordinal);

        Assert.Throws<InvalidDataException>(() => Read(Document(malformed)));
    }

    [Fact]
    public void Incomplete_file_without_EOF_is_rejected()
    {
        string incomplete = Document(Triangle).Replace("0\nEOF\n", "", StringComparison.Ordinal);

        Assert.Throws<InvalidDataException>(() => Read(incomplete));
    }

    [Fact]
    public void Incomplete_group_pair_is_rejected()
    {
        Assert.Throws<InvalidDataException>(() => Read(Document(Triangle) + "10\n"));
    }

    [Fact]
    public void Binary_DXF_is_rejected_explicitly()
    {
        var exception = Assert.Throws<NotSupportedException>(() =>
            WithFile(Encoding.ASCII.GetBytes("AutoCAD Binary DXF\r\n\x1a\0"), DxfFaceSource.Read));

        Assert.Contains("Двоичный DXF", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Referenced_nested_block_faces_are_rejected_instead_of_losing_their_transform()
    {
        string blocks =
            "0\nBLOCK\n2\ninner\n" + Triangle + "0\nENDBLK\n" +
            "0\nBLOCK\n2\nouter\n0\nINSERT\n2\ninner\n0\nENDBLK\n";
        var exception = Assert.Throws<NotSupportedException>(() =>
            Read(Document("0\nINSERT\n2\nouter\n", blocks: blocks)));

        Assert.Contains("outer", exception.Message, StringComparison.Ordinal);
        Assert.Contains("3DFACE", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Referenced_palette_block_without_faces_does_not_block_direct_model_faces()
    {
        string blocks =
            "0\nBLOCK\n2\npalette\n" +
            "0\nSOLID\n8\ncalc\n10\n0\n20\n0\n11\n1\n21\n0\n12\n0\n22\n1\n13\n1\n23\n1\n" +
            "0\nENDBLK\n";
        var source = Read(Document("0\nINSERT\n2\npalette\n" + Triangle, blocks: blocks));

        Assert.Single(source.Faces);
        Assert.Equal(0, source.BlockFaceCount);
        Assert.Equal(1, source.EntityCounts["INSERT"]);
    }

    [Fact]
    public void Paperspace_faces_are_counted_but_not_used_as_model_demand_regions()
    {
        string modelFace = Triangle;
        string paperFace = Triangle.Replace("FACE_A", "PAPER_A", StringComparison.Ordinal) + "67\n1\n";
        var source = Read(Document(modelFace + paperFace));

        Assert.Equal("FACE_A", Assert.Single(source.Faces).Id);
        Assert.Equal(1, source.IgnoredPaperSpaceFaceCount);
        Assert.Equal(2, source.EntityCounts["3DFACE"]);
    }

    [Theory]
    [InlineData("MESH", 0)]
    [InlineData("POLYLINE", 16)]
    [InlineData("POLYLINE", 64)]
    [InlineData("POLYLINE", 80)]
    public void Unsupported_model_meshes_cannot_be_silently_omitted(string entity, int flags)
    {
        var exception = Assert.Throws<NotSupportedException>(() =>
            Read(Document(Triangle + $"0\n{entity}\n70\n{flags}\n")));
        Assert.Contains(entity, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Ordinary_polylines_and_paperspace_meshes_do_not_block_calculation_faces()
    {
        var source = Read(Document(Triangle + "0\nPOLYLINE\n70\n8\n0\nMESH\n67\n1\n"));
        Assert.Single(source.Faces);
    }

    [Fact]
    public void Unsupported_mesh_in_a_referenced_nested_block_is_rejected()
    {
        string blocks = "0\nBLOCK\n2\ninner\n0\nMESH\n0\nENDBLK\n" +
            "0\nBLOCK\n2\nouter\n0\nINSERT\n2\ninner\n0\nENDBLK\n";
        var exception = Assert.Throws<NotSupportedException>(() =>
            Read(Document(Triangle + "0\nINSERT\n2\nouter\n", blocks: blocks)));
        Assert.Contains("outer", exception.Message, StringComparison.Ordinal);
    }

    private static string Document(string entities, string header = "", string blocks = "") =>
        "0\nSECTION\n2\nHEADER\n9\n$ACADVER\n1\nAC1009\n" + header + "0\nENDSEC\n" +
        "0\nSECTION\n2\nTABLES\n0\nLAYER\n2\ncalc\n62\n3\n0\nENDSEC\n" +
        (blocks.Length == 0 ? "" : "0\nSECTION\n2\nBLOCKS\n" + blocks + "0\nENDSEC\n") +
        "0\nSECTION\n2\nENTITIES\n" + entities + "0\nENDSEC\n0\nEOF\n";

    private static DxfSource Read(string contents) => WithFile(Encoding.UTF8.GetBytes(contents), DxfFaceSource.Read);

    private static T WithFile<T>(byte[] contents, Func<string, T> operation)
    {
        string path = Path.Combine(Path.GetTempPath(), "citrus-dxf-test-" + Guid.NewGuid().ToString("N") + ".dxf");
        try
        {
            File.WriteAllBytes(path, contents);
            return operation(path);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
