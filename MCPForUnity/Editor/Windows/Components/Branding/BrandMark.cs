using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace MCPForUnity.Editor.Windows.Components.Branding
{
    /// <summary>
    /// The Unity MCP Light brand mark (the node cube from docs/images/unity-mcp-light-logo.png).
    ///
    /// Drawn from the raster icons that ship at the package root: package-icon.png for the light
    /// editor skin and package-icon-dark.png (slate strokes lifted to light grey) for the dark
    /// skin. Size the element via USS (width/height); the texture scales to fit.
    /// </summary>
    public sealed class BrandMark : Image
    {
        public BrandMark()
        {
            image = LoadBrandTexture();
            scaleMode = ScaleMode.ScaleToFit;
            pickingMode = PickingMode.Ignore; // purely decorative
        }

        private static Texture2D LoadBrandTexture()
        {
            try
            {
                string root = MCPForUnity.Editor.Helpers.AssetPathUtility.GetMcpPackageRootPath();
                string file = EditorGUIUtility.isProSkin ? "package-icon-dark.png" : "package-icon.png";
                return AssetDatabase.LoadAssetAtPath<Texture2D>($"{root}/{file}")
                    ?? AssetDatabase.LoadAssetAtPath<Texture2D>($"{root}/package-icon.png");
            }
            catch (System.Exception ex)
            {
                MCPForUnity.Editor.Helpers.McpLog.Warn($"BrandMark: failed to load brand icon: {ex.Message}");
                return null;
            }
        }
    }
}
