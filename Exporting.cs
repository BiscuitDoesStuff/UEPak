using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse_Conversion.Options;
using CUE4Parse_Conversion.Writers.UEFormat.Enums;

static class Exporting
{
    public static ExportOptions Options(EMeshFormat meshFormat, bool exportMaterials = false) => new(
        meshFormat, ENaniteMeshFormat.NaniteFirst, EMeshQuality.Highest, ETexturePlatform.DesktopMobile, ETextureFormat.Png,
        100, false, false, EMaterialDepth.AllLayers, exportMaterials, false, ESocketFormat.Bone, EFileCompressionFormat.None);
}
