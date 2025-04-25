using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Graphics.PackedVector;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using ReLogic.Content;
using ReLogic.Threading;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using Terraria;
using Terraria.GameContent;
using Terraria.GameContent.Drawing;
using Terraria.Graphics;
using Terraria.Graphics.Light;
using Terraria.ID;
using Terraria.ModLoader;

namespace LiquidShapesPatch.Common;

public partial class LiquidRenderFixSystem : ModSystem
{
    public static Asset<Effect> MaskEffect;

    public static event Action PreRenderLiquid;
    public static event Action PostRenderLiquid;

    public static bool FixRendering { get; set; } // May want to check for certain mods first

    public override void Load()
    {
        if (Main.dedServ)
            return;

        MaskEffect = ModContent.Request<Effect>($"{nameof(LiquidShapesPatch)}/Assets/Effects/ImageMask", AssetRequestMode.ImmediateLoad);
        
        FixRendering = true;

        GetScreenDrawArea = Main.instance.TilesRenderer.GetType().GetMethod("GetScreenDrawArea", BindingFlags.NonPublic | BindingFlags.Instance)
            .CreateDelegate<GetScreenDrawAreaDelegate>(Main.instance.TilesRenderer);

        DrawWaters = typeof(Main).GetMethod("DrawWaters", BindingFlags.NonPublic | BindingFlags.Instance)
            .CreateDelegate<DrawWatersDelegate>(Main.instance);

        Main.OnRenderTargetsInitialized += InitTargets;
        Main.OnRenderTargetsReleased += ReleaseTargets;

        IL_Main.DoDraw += AddEventsToDraw;
        On_Main.DrawLiquid += DrawLiquid;
        On_Main.DoDraw_UpdateCameraPosition += PrepareTargets;
        On_Main.RenderWater += RenderWaterOverride;
    }

    public delegate void GetScreenDrawAreaDelegate(Vector2 screenPosition, Vector2 offSet, out int firstTileX, out int lastTileX, out int firstTileY, out int lastTileY);
    public static GetScreenDrawAreaDelegate GetScreenDrawArea;
    public delegate void DrawWatersDelegate(bool isBackground = false);
    public static DrawWatersDelegate DrawWaters;

    private static HashSet<Point> _edgeTiles = new HashSet<Point>();
    private static HashSet<Point> _waterPlants = new HashSet<Point>();

    public static void GetCuttingTiles()
    {
        Vector2 unscaledPosition = Main.Camera.UnscaledPosition;
        Vector2 screenOff = new Vector2(Main.drawToScreen ? 0 : Main.offScreenRange);
        GetScreenDrawArea(unscaledPosition, screenOff, out int left, out int right, out int top, out int bottom);

        _waterPlants.Clear();
        _edgeTiles.Clear();

        for (int i = left; i < right; i++)
        {
            for (int j = top; j < bottom; j++)
            {
                if (!WorldGen.InWorld(i, j))
                    continue;

                if (Main.tile[i, j].HasTile && Main.tile[i, j].TileType == TileID.LilyPad)
                    _waterPlants.Add(new Point(i, j));

                if (WorldGen.SolidOrSlopedTile(i, j))
                {
                    bool foundValid = false;

                    if (WorldGen.InWorld(i, j + 1))
                    {
                        if (Main.tile[i, j + 1].LiquidAmount >= 255)
                            foundValid = true;
                    }
                    if (WorldGen.InWorld(i, j - 1))
                    {
                        if (Main.tile[i, j - 1].LiquidAmount > 0)
                            foundValid = true;
                    }
                    if (WorldGen.InWorld(i - 1, j))
                    {
                        if (Main.tile[i - 1, j].LiquidAmount > 0)
                            foundValid = true;
                    }
                    if (WorldGen.InWorld(i + 1, j))
                    {
                        if (Main.tile[i + 1, j].LiquidAmount > 0)
                            foundValid = true;
                    }                          
                    if (WorldGen.InWorld(i - 1, j - 1))
                    {
                        if (Main.tile[i - 1, j - 1].LiquidAmount > 0)
                            foundValid = true;
                    }
                    if (WorldGen.InWorld(i + 1, j - 1))
                    {
                        if (Main.tile[i + 1, j - 1].LiquidAmount > 0)
                            foundValid = true;
                    }                    

                    if (foundValid)
                        _edgeTiles.Add(new Point(i, j));
                }
            }
        }
    }

    private void AddEventsToDraw(ILContext il)
    {
        try
        {
            ILCursor c = new ILCursor(il);

            c.TryGotoNext(i => i.MatchLdsfld<Main>("waterTarget"));
            c.TryGotoNext(i => i.MatchCallvirt<SpriteBatch>("Draw"));
            c.Index++;
            ILLabel label = il.DefineLabel(c.Next);
            c.TryGotoPrev(i => i.MatchLdsfld<Main>("waterTarget"));
            c.Emit(OpCodes.Pop);
            c.EmitDelegate(DrawWater);
            c.Emit(OpCodes.Br, label);
        }
        catch
        {
            MonoModHooks.DumpIL(Mod, il);
            Mod.Logger.Error("Water target was unable to be replaced.");
            FixRendering = false;
        }
    }

    private void DrawWater()
    {
        PreRenderLiquid?.Invoke();
        Main.spriteBatch.Draw(Main.waterTarget, Main.sceneWaterPos - Main.screenPosition, Color.White);
        PostRenderLiquid?.Invoke();
    }

    public static RenderTarget2D liquidTargetNoCut;
    public static RenderTarget2D liquidTarget;
    public static RenderTarget2D liquidMaskTarget;

    private static bool _ready;

    private void InitTargets(int width, int height)
    {
        width += Main.offScreenRange * 2;
        height += Main.offScreenRange * 2;
        try
        {
            liquidTarget = new RenderTarget2D(Main.instance.GraphicsDevice, width, height, mipMap: false, Main.instance.GraphicsDevice.PresentationParameters.BackBufferFormat, DepthFormat.None);
            liquidTargetNoCut = new RenderTarget2D(Main.instance.GraphicsDevice, width, height, mipMap: false, Main.instance.GraphicsDevice.PresentationParameters.BackBufferFormat, DepthFormat.None);
            liquidMaskTarget = new RenderTarget2D(Main.instance.GraphicsDevice, width, height, mipMap: false, Main.instance.GraphicsDevice.PresentationParameters.BackBufferFormat, DepthFormat.None);

            _ready = true;
        }
        catch (Exception ex)
        {
            Lighting.Mode = LightMode.Retro;
            Console.WriteLine("Failed to create liquid rendering render targets. " + ex);
            _ready = false;
        }
    }

    private void ReleaseTargets()
    {
        _ready = false;

        try
        {
            liquidTarget?.Dispose();
            liquidTargetNoCut?.Dispose();
            liquidMaskTarget?.Dispose();
        }
        catch (Exception ex)
        {
            Console.WriteLine("Error disposing liquid rendering render targets. " + ex);
            FixRendering = false;
        }

        liquidTarget = null;
        liquidTargetNoCut = null;
        liquidMaskTarget = null;
    }

    private void RenderWaterOverride(On_Main.orig_RenderWater orig, Main self)
    {
        if (FixRendering && !Main.drawToScreen)
        {
            self.GraphicsDevice.SetRenderTarget(liquidTarget);
            self.GraphicsDevice.Clear(Color.Transparent);
            Main.spriteBatch.Begin();

            try
            {
                DrawWaters();
            }
            catch
            {
            }

            TimeLogger.DetailedDrawReset();
            Main.spriteBatch.End();
            TimeLogger.DetailedDrawTime(31);

            self.GraphicsDevice.SetRenderTarget(null);

        }
        else
            orig(self);
    }

    private void PrepareTargets(On_Main.orig_DoDraw_UpdateCameraPosition orig)
    {
        orig();

        if (Main.renderCount != 1 || !_ready)
            return;

        Vector2 unscaledPosition = Main.Camera.UnscaledPosition;
        Vector2 offScreen = new Vector2(Main.drawToScreen ? 0 : Main.offScreenRange);

        GetCuttingTiles();

        Main.instance.GraphicsDevice.SetRenderTarget(liquidMaskTarget);
        Main.instance.GraphicsDevice.Clear(Color.Transparent);
        Main.tileBatch.Begin();

        foreach (Point point in _edgeTiles)
            DrawSingleTile(point.X, point.Y, Main.screenPosition);

        Main.tileBatch.End();
        Main.spriteBatch.Begin();

        foreach (Point point in _waterPlants)
            Main.DrawTileInWater(-Main.screenPosition, point.X, point.Y);

        Main.spriteBatch.End();
        Main.instance.GraphicsDevice.SetRenderTarget(liquidTargetNoCut);
        Main.instance.GraphicsDevice.Clear(Color.Transparent);
        Main.spriteBatch.Begin();

        Main.spriteBatch.Draw(liquidTarget, Main.sceneWaterPos - Main.screenPosition, Color.White);

        Main.spriteBatch.End();
        Main.instance.GraphicsDevice.SetRenderTarget(Main.waterTarget);
        Main.instance.GraphicsDevice.Clear(Color.Transparent);
        Main.spriteBatch.Begin(SpriteSortMode.Immediate, BlendState.AlphaBlend);

        Effect mask = MaskEffect.Value;
        mask.Parameters["uMaskAdd"].SetValue(liquidTargetNoCut);
        mask.Parameters["uMaskSubtract"].SetValue(liquidMaskTarget);
        mask.Parameters["uMaskColor"].SetValue(1);
        mask.Parameters["useAlpha"].SetValue(false);
        mask.Parameters["useColor"].SetValue(false);
        mask.CurrentTechnique.Passes[0].Apply(); 
        Main.spriteBatch.Draw(liquidTargetNoCut, offScreen, Color.White);

        Main.spriteBatch.End();
        Main.instance.GraphicsDevice.SetRenderTarget(null);
        Main.instance.GraphicsDevice.Clear(Color.Transparent);
    }

    public static void DrawSingleTile(int i, int j, Vector2 offset)
    {
        Tile tile = Main.tile[i, j];
        Main.instance.LoadTiles(tile.TileType);
        Texture2D tileTexture = TextureAssets.Tile[tile.TileType].Value;
        Rectangle tileFrame = new Rectangle(tile.TileFrameX, tile.TileFrameY, 16, 16);
        Vector2 tilePos = new Vector2(i * 16f, j * 16f);
        VertexColors color = new VertexColors(Color.White);
        if ((tile.Slope == 0 || TileID.Sets.HasSlopeFrames[tile.TileType]) && !tile.IsHalfBlock)
        {
            if (!TileID.Sets.IgnoresNearbyHalfbricksWhenDrawn[tile.TileType] && (Main.tile[i - 1, j].IsHalfBlock || Main.tile[i + 1, j].IsHalfBlock))
            {
                int frameOff = 4;
                if (TileID.Sets.AllBlocksWithSmoothBordersToResolveHalfBlockIssue[tile.TileType])
                    frameOff = 2;

                if (Main.tile[i - 1, j].IsHalfBlock)
                {
                    Main.tileBatch.Draw(tileTexture, tilePos + new Vector2(0f, 8f) - offset, new Rectangle(tile.TileFrameX, tile.TileFrameY + 8, 16, 8), color, Vector2.Zero, 1f, 0);
                    Main.tileBatch.Draw(tileTexture, tilePos + new Vector2(frameOff, 0f) - offset, new Rectangle(tile.TileFrameX + frameOff, tile.TileFrameY, 16 - frameOff, 16), color, Vector2.Zero, 1f, 0);
                    Main.tileBatch.Draw(tileTexture, tilePos - offset, new Rectangle(144, 0, frameOff, 8), color, Vector2.Zero, 1f, 0);
                    if (frameOff == 2)
                        Main.tileBatch.Draw(tileTexture, tilePos - offset, new Rectangle(148, 0, 2, 2), color, Vector2.Zero, 1f, 0);
                }
                else if (Main.tile[i + 1, j].IsHalfBlock)
                {
                    Main.tileBatch.Draw(tileTexture, tilePos + new Vector2(0f, 8f) - offset, new Rectangle(tile.TileFrameX, tile.TileFrameY + 8, 16, 8), color, Vector2.Zero, 1f, 0);
                    Main.tileBatch.Draw(tileTexture, tilePos - offset, new Rectangle(tile.TileFrameX, tile.TileFrameY, 16 - frameOff, 16), color, Vector2.Zero, 1f, 0);
                    Main.tileBatch.Draw(tileTexture, tilePos + new Vector2(16 - frameOff, 0f) - offset, new Rectangle(144 + (16 - frameOff), 0, frameOff, 8), color, Vector2.Zero, 1f, 0);
                    if (frameOff == 2)
                        Main.tileBatch.Draw(tileTexture, tilePos + new Vector2(14f, 0f) - offset, new Rectangle(156, 0, 2, 2), color, Vector2.Zero, 1f, 0);
                }
            }
            else
            {
                Main.tileBatch.Draw(tileTexture, tilePos - offset, tileFrame, color, Vector2.Zero, 1f, 0);
            }
        }
        else if (tile.IsHalfBlock)
        {
            tilePos.Y += 8;
            tileFrame.Height -= 8;
            Main.tileBatch.Draw(tileTexture, tilePos - offset, tileFrame, color, Vector2.Zero, 1f, 0);
        }
        else
        {
            for (int iSlope = 0; iSlope < 8; iSlope++)
            {
                int num3 = iSlope * -2;
                int num4 = 16 - iSlope * 2;
                int num5 = 16 - num4;
                int num6;
                switch ((int)tile.Slope)
                {
                    case 1:
                        num3 = 0;
                        num6 = iSlope * 2;
                        num4 = 14 - iSlope * 2;
                        num5 = 0;
                        break;
                    case 2:
                        num3 = 0;
                        num6 = 16 - iSlope * 2 - 2;
                        num4 = 14 - iSlope * 2;
                        num5 = 0;
                        break;
                    case 3:
                        num6 = iSlope * 2;
                        break;
                    default:
                        num6 = 16 - iSlope * 2 - 2;
                        break;
                }
                Main.tileBatch.Draw(tileTexture, tilePos + new Vector2(num6, iSlope * 2 + num3) - offset, new Rectangle(tile.TileFrameX + num6, tile.TileFrameY + num5, 2, num4), color, Vector2.Zero, 1f, 0);
            }

            int bottomOff = (int)tile.Slope <= 2 ? 14 : 0;
            Main.tileBatch.Draw(tileTexture, tilePos + new Vector2(0, bottomOff) - offset, new Rectangle(tile.TileFrameX, tile.TileFrameY + bottomOff, 16, 2), color, Vector2.Zero, 1f, 0);
        }
    }

    public void Reset()
    {
    }
}