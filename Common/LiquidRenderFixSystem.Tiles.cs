using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics.PackedVector;
using System;
using System.Diagnostics;
using System.Reflection;
using Terraria;
using Terraria.GameContent;
using Terraria.GameContent.Drawing;
using Terraria.GameContent.Liquid;
using Terraria.Graphics;
using Terraria.Graphics.Light;
using Terraria.ID;
using Terraria.ModLoader;

namespace LiquidShapesPatch.Common;

public partial class LiquidRenderFixSystem : ModSystem
{
    private static bool DrawTileLiquid;

    private static bool IsInDrawBlack(int tileX, int tileY, Tile tileCache)
    {
        int totalColor = (Main.tileColor.R + Main.tileColor.G + Main.tileColor.B) / 3;
        float color = (float)((double)totalColor * 0.4) / 255f;
        bool flag2 = tileY >= Main.UnderworldLayer;
        if (flag2)
            color = 0.2f;

        float brightness = Lighting.Brightness(tileX, tileY);
        brightness = (float)Math.Floor(brightness * 255f) / 255f;
        byte liquidAmont = tileCache.LiquidAmount;
        return brightness <= color && ((!flag2 && liquidAmont < 250) || WorldGen.SolidTile(tileCache) || (liquidAmont >= 200 && brightness == 0f));
    }

    private void CeaseLiquidInTileDraw(On_TileDrawing.orig_DrawTile_LiquidBehindTile orig, TileDrawing self, bool solidLayer, bool inFrontOfPlayers, int waterStyleOverride, Vector2 screenPosition, Vector2 screenOffset, int tileX, int tileY, Tile tileCache)
    {
        if (FixRendering)
        {
            if ((IsInDrawBlack(tileX, tileY, tileCache) || (DrawTileLiquid && !solidLayer)) && tileY < Main.worldSurface && tileCache.WallType == WallID.None)
                orig(self, !solidLayer, inFrontOfPlayers, waterStyleOverride, screenPosition, screenOffset, tileX, tileY, tileCache);
        }
        else
            orig(self, solidLayer, inFrontOfPlayers, waterStyleOverride, screenPosition, screenOffset, tileX, tileY, tileCache);
    }

    private void DrawLiquid(On_Main.orig_DrawLiquid orig, Main self, bool bg, int waterStyle, float Alpha, bool drawSinglePassLiquids)
    {
        if (FixRendering)
        {
            if (!Lighting.NotRetro)
            {
                Main.instance.oldDrawWater(bg, waterStyle, Alpha);
                return;
            }

            DrawTileLiquid = false;

            Stopwatch stopwatch = new Stopwatch();
            stopwatch.Start();
            Vector2 drawOffset = (Main.drawToScreen ? Vector2.Zero : new Vector2(Main.offScreenRange)) - Main.screenPosition;

            //Main.instance.TilesRenderer.DrawLiquidBehindTiles();
            DrawLiquidOverTiles(waterStyle, Alpha, bg);

            LiquidRenderer.Instance.DrawNormalLiquids(Main.spriteBatch, drawOffset, waterStyle, Alpha, bg);
            if (drawSinglePassLiquids)
                LiquidRenderer.Instance.DrawShimmer(Main.spriteBatch, drawOffset, bg);

            if (!bg)
                TimeLogger.DrawTime(4, stopwatch.Elapsed.TotalMilliseconds);

            DrawTileLiquid = false;
        }
        else
            orig(self, bg, waterStyle, Alpha, drawSinglePassLiquids);
    }

    private static void DrawLiquidOverTiles(int waterStyle, float alpha, bool bg = false)
    {
        Vector2 unscaledPosition = Main.Camera.UnscaledPosition;
        Vector2 screenOff = new Vector2(Main.drawToScreen ? 0 : Main.offScreenRange);

        Main.instance.TilesRenderer.GetScreenDrawArea(unscaledPosition, screenOff + (Main.Camera.UnscaledPosition - Main.Camera.ScaledPosition), out int left, out int right, out int top, out int bottom);

        for (int j = top; j < bottom; j++)
        {
            for (int i = left; i < right; i++)
            {
                if (WorldGen.SolidOrSlopedTile(i, j))
                {
                    int liquidType = -1;
                    int liquidAmount = 0;
                    bool onRight = false;
                    bool onLeft = false;
                    bool onTop = false;
                    bool onBottom = false;
                    Vector2 liquidPos = new Vector2(i * 16, j * 16);
                    Rectangle liquidFrame = new Rectangle(0, 4, 16, 16);

                    if (Main.tile[i, j + 1].LiquidAmount >= 240) // basically guarantees a full liquid block
                    {
                        liquidAmount = 255;
                        liquidType = Main.tile[i, j + 1].LiquidType;
                        onBottom = true;
                    }

                    if (Main.tile[i, j - 1].LiquidAmount > 0) // can't really determine how much liquid to have
                    {
                        liquidAmount = Math.Max(liquidAmount, Main.tile[i, j].LiquidAmount);
                        liquidType = Main.tile[i, j - 1].LiquidType;
                        onTop = true;
                    }

                    if (Main.tile[i - 1, j].LiquidAmount > 0) // copy from side
                    {
                        if (Main.tile[i - 1, j].LiquidAmount < 240)
                            liquidAmount = Main.tile[i - 1, j].LiquidAmount;
                        else
                            liquidAmount = 255;

                        liquidType = Main.tile[i - 1, j].LiquidType;
                        onLeft = true;
                    }

                    if (Main.tile[i + 1, j].LiquidAmount > 0) // copy from side
                    {
                        if (Main.tile[i + 1, j].LiquidAmount < 240)
                            liquidAmount = Main.tile[i + 1, j].LiquidAmount;
                        else
                            liquidAmount = 255;

                        liquidType = Main.tile[i + 1, j].LiquidType;
                        onRight = true;
                    }

                    if (onLeft || onRight || onTop || onBottom)
                    {
                        if (Main.tile[i, j - 1].Slope != 0)
                        {
                            liquidFrame.Height -= 4;
                            liquidPos.Y += 4;
                        }

                        if (Main.tile[i, j].IsHalfBlock)
                        {
                            if (!onTop && (onLeft || onRight))
                            {
                                liquidFrame.Height = (int)(liquidAmount / 255f * 16f);
                                liquidPos.Y += 16 - liquidAmount / 16;
                            }
                            else
                            {
                                liquidFrame.Height = 8;
                                liquidPos.Y += 8;
                            }
                        }
                        else
                        {
                            if (Main.tile[i, j].Slope == 0)
                            {
                                if (onTop && !(onBottom || onLeft || onRight))
                                    liquidFrame.Height = 8;

                                if (onBottom && !(onTop || onLeft || onRight))
                                {
                                    liquidFrame.Height = 8;
                                    liquidPos.Y += 8;
                                }

                                if (onLeft && !(onTop || onBottom || onRight))
                                    liquidFrame.Width = 8;

                                if (onRight && !(onTop || onBottom || onLeft))
                                {
                                    liquidFrame.Width = 8;
                                    liquidPos.X += 8;
                                }
                            }
                            else if (!onBottom)
                                liquidFrame.Height += 4;

                            if ((onLeft || onRight) && !onTop)
                            {
                                liquidFrame.Height += (int)(liquidAmount / 255f * 16f) - 16;
                                liquidPos.Y += 16 - (int)(liquidAmount / 255f * 16f);
                            }
                        }

                        int realStyle = waterStyle;
                        switch (liquidType)
                        {
                            default:
                            case LiquidID.Water:
                                realStyle = waterStyle;
                                break;
                            case LiquidID.Lava:
                                realStyle = WaterStyleID.Lava;
                                break;
                            case LiquidID.Honey:
                                realStyle = WaterStyleID.Honey;
                                break;
                            case LiquidID.Shimmer:
                                realStyle = 14;
                                break;
                        }

                        DrawLiquidTile(i, j, liquidType, realStyle, liquidPos - Main.screenPosition + new Vector2(Main.drawToScreen ? 0 : Main.offScreenRange), liquidFrame, alpha, bg);
                    }
                }
            }
        }
    }


    private static void DrawLiquidTile(int i, int j, int liquidType, int waterStyle, Vector2 position, Rectangle frame, float alpha, bool bg)
    {
        Lighting.GetCornerColors(i, j, out VertexColors colors);

        float liquidOpacity = LiquidRenderer.DEFAULT_OPACITY[liquidType];

        if (liquidType == LiquidID.Shimmer)
            LiquidRenderer.SetShimmerVertexColors(ref colors, bg ? 1f : alpha * liquidOpacity, i, j);
        else
        {
            if (!bg)
            {
                colors.TopLeftColor *= liquidOpacity;
                colors.TopRightColor *= liquidOpacity;
                colors.BottomLeftColor *= liquidOpacity;
                colors.BottomRightColor *= liquidOpacity;

                if (Main.tile[i, j].IsHalfBlock && Main.tile[i, j - 1].LiquidAmount > 0)
                {
                    colors.TopLeftColor = colors.TopLeftColor.MultiplyRGBA(new Color(215, 215, 215));
                    colors.TopRightColor = colors.TopRightColor.MultiplyRGBA(new Color(215, 215, 215));
                    colors.BottomLeftColor = colors.BottomLeftColor.MultiplyRGBA(new Color(215, 215, 215));
                    colors.BottomRightColor = colors.BottomRightColor.MultiplyRGBA(new Color(215, 215, 215));
                }
            }
        }

        Main.instance.TilesRenderer.DrawPartialLiquid(bg, Main.tile[i, j], ref position, ref frame, waterStyle, ref colors);
    }
}