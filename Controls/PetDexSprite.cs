using Athena.UI.Services;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using System;

namespace Athena.UI.Controls;

/// <summary>Draws one frame from a local PetDex spritesheet without interpolation.</summary>
public sealed class PetDexSprite : Control
{
    public static readonly StyledProperty<string> PetSlugProperty =
        AvaloniaProperty.Register<PetDexSprite, string>(nameof(PetSlug), PetDexPetLibrary.DefaultSlug);

    public static readonly StyledProperty<PetDexAnimationState> AnimationStateProperty =
        AvaloniaProperty.Register<PetDexSprite, PetDexAnimationState>(nameof(AnimationState));

    public static readonly StyledProperty<int> FrameIndexProperty =
        AvaloniaProperty.Register<PetDexSprite, int>(nameof(FrameIndex));

    public string PetSlug
    {
        get => GetValue(PetSlugProperty);
        set => SetValue(PetSlugProperty, value);
    }

    public PetDexAnimationState AnimationState
    {
        get => GetValue(AnimationStateProperty);
        set => SetValue(AnimationStateProperty, value);
    }

    public int FrameIndex
    {
        get => GetValue(FrameIndexProperty);
        set => SetValue(FrameIndexProperty, value);
    }

    static PetDexSprite()
    {
        AffectsRender<PetDexSprite>(PetSlugProperty, AnimationStateProperty, FrameIndexProperty);
    }

    public PetDexSprite()
    {
        RenderOptions.SetBitmapInterpolationMode(this, BitmapInterpolationMode.None);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (Bounds.Width <= 0 || Bounds.Height <= 0) return;

        var pet = PetDexPetLibrary.Resolve(PetSlug);
        var frameCount = pet.FrameCount(AnimationState);
        var frame = Math.Clamp(FrameIndex, 0, frameCount - 1);
        var row = pet.RowIndex(AnimationState);
        var source = new Rect(
            frame * pet.FrameWidth,
            row * pet.FrameHeight,
            pet.FrameWidth,
            pet.FrameHeight);
        context.DrawImage(pet.SpriteSheet, source, new Rect(Bounds.Size));
    }
}
