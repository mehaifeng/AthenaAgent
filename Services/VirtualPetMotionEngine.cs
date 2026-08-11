using Athena.UI.Models;
using System;

namespace Athena.UI.Services;

/// <summary>
/// Framework-independent motion model for the in-app pet. Coordinates are offsets
/// from its bottom-right layout anchor, so zero is the normal resting position.
/// </summary>
public sealed class VirtualPetMotionEngine
{
    private const double Gravity = 1350;
    private readonly Random _random;
    private double _fullMinX;
    private double _fullMinY;
    private double _roamMinY;
    private double _throwVelocityX;
    private double _throwVelocityY;
    private double _walkVelocityX;
    private double _walkVelocityY;
    private double _roamTime;
    private double _roamWait = 1.2;

    public VirtualPetMotionEngine(int? randomSeed = null)
    {
        _random = randomSeed.HasValue ? new Random(randomSeed.Value) : Random.Shared;
    }

    public double X { get; private set; }
    public double Y { get; private set; }
    public double HorizontalVelocity => _throwVelocityX + _walkVelocityX;
    public double VerticalVelocity => _throwVelocityY + _walkVelocityY;
    public bool IsDragging { get; private set; }

    public void SetBounds(
        double width,
        double height,
        double petWidth,
        double petHeight,
        VirtualPetRoamArea area)
    {
        _fullMinX = -Math.Max(0, width - petWidth - 20);
        _fullMinY = -Math.Max(0, height - petHeight - 12);
        _roamMinY = area switch
        {
            VirtualPetRoamArea.BottomEdge => 0,
            VirtualPetRoamArea.LowerHalf => -Math.Max(0, height * 0.5 - petHeight),
            _ => _fullMinY
        };
        X = Math.Clamp(X, _fullMinX, 0);
        Y = Math.Clamp(Y, _fullMinY, 0);
    }

    public void BeginDrag()
    {
        IsDragging = true;
        _walkVelocityX = 0;
        _walkVelocityY = 0;
        _throwVelocityX = 0;
        _throwVelocityY = 0;
        _roamTime = 0;
    }

    public void DragTo(double x, double y, double elapsedSeconds)
    {
        if (!IsDragging) return;
        var nextX = Math.Clamp(x, _fullMinX, 0);
        var nextY = Math.Clamp(y, _fullMinY, 0);
        if (elapsedSeconds is > 0.001 and < 0.25)
        {
            var instantX = (nextX - X) / elapsedSeconds;
            var instantY = (nextY - Y) / elapsedSeconds;
            _throwVelocityX = Math.Clamp(_throwVelocityX * 0.45 + instantX * 0.55, -900, 900);
            _throwVelocityY = Math.Clamp(_throwVelocityY * 0.45 + instantY * 0.55, -900, 900);
        }
        X = nextX;
        Y = nextY;
    }

    public void EndDrag(bool gravityEnabled)
    {
        IsDragging = false;
        if (!gravityEnabled) Y = Math.Clamp(Y, _roamMinY, 0);
        _roamWait = 0.7;
    }

    public void Tick(
        double elapsedSeconds,
        bool roamingEnabled,
        bool gravityEnabled,
        bool canRoam)
    {
        if (IsDragging) return;
        var dt = Math.Clamp(elapsedSeconds, 0, 0.05);
        if (dt <= 0) return;

        UpdateRoaming(dt, roamingEnabled && canRoam, gravityEnabled);

        X += (_throwVelocityX + _walkVelocityX) * dt;
        if (gravityEnabled)
        {
            _throwVelocityY += Gravity * dt;
            Y += _throwVelocityY * dt;
        }
        else
        {
            Y += (_throwVelocityY + _walkVelocityY) * dt;
        }

        ResolveHorizontalCollision();
        if (gravityEnabled)
        {
            if (Y >= 0)
            {
                Y = 0;
                if (_throwVelocityY > 170)
                    _throwVelocityY = -_throwVelocityY * 0.22;
                else
                    _throwVelocityY = 0;
            }
            if (Y < _fullMinY)
            {
                Y = _fullMinY;
                _throwVelocityY = Math.Abs(_throwVelocityY) * 0.2;
            }
            _walkVelocityY = 0;
        }
        else
        {
            ResolveVerticalCollision();
        }

        var damping = Math.Pow(0.08, dt);
        _throwVelocityX *= damping;
        if (!gravityEnabled) _throwVelocityY *= damping;
        if (Math.Abs(_throwVelocityX) < 1) _throwVelocityX = 0;
        if (!gravityEnabled && Math.Abs(_throwVelocityY) < 1) _throwVelocityY = 0;
    }

    public void StopAutomaticMotion()
    {
        _walkVelocityX = 0;
        _walkVelocityY = 0;
        _roamTime = 0;
        _roamWait = 0.8;
    }

    private void UpdateRoaming(double dt, bool enabled, bool gravityEnabled)
    {
        if (!enabled)
        {
            StopAutomaticMotion();
            return;
        }
        if (_roamWait > 0)
        {
            _roamWait -= dt;
            _walkVelocityX = 0;
            _walkVelocityY = 0;
            return;
        }
        if (_roamTime <= 0)
        {
            var speed = 28 + _random.NextDouble() * 34;
            _walkVelocityX = (_random.Next(2) == 0 ? -1 : 1) * speed;
            if (gravityEnabled)
            {
                // Under gravity, vertical roaming becomes a natural hop. The configured
                // roaming area's upper edge caps the jump height; BottomEdge stays grounded.
                _walkVelocityY = 0;
                if (_roamMinY < 0 && Y >= -1)
                {
                    var targetHeight = -_roamMinY * (0.14 + _random.NextDouble() * 0.14);
                    _throwVelocityY = -Math.Sqrt(2 * Gravity * targetHeight);
                }
            }
            else
            {
                _walkVelocityY = _roamMinY < 0
                    ? (_random.NextDouble() - 0.5) * speed * 0.65
                    : 0;
            }
            _roamTime = 0.9 + _random.NextDouble() * 1.8;
        }
        _roamTime -= dt;
        if (_roamTime <= 0)
        {
            _walkVelocityX = 0;
            _walkVelocityY = 0;
            _roamWait = 0.7 + _random.NextDouble() * 2.2;
        }
    }

    private void ResolveHorizontalCollision()
    {
        if (X < _fullMinX)
        {
            X = _fullMinX;
            _throwVelocityX = Math.Abs(_throwVelocityX) * 0.35;
            _walkVelocityX = Math.Abs(_walkVelocityX);
        }
        else if (X > 0)
        {
            X = 0;
            _throwVelocityX = -Math.Abs(_throwVelocityX) * 0.35;
            _walkVelocityX = -Math.Abs(_walkVelocityX);
        }
    }

    private void ResolveVerticalCollision()
    {
        if (Y < _roamMinY)
        {
            Y = _roamMinY;
            _throwVelocityY = Math.Abs(_throwVelocityY) * 0.3;
            _walkVelocityY = Math.Abs(_walkVelocityY);
        }
        else if (Y > 0)
        {
            Y = 0;
            _throwVelocityY = -Math.Abs(_throwVelocityY) * 0.3;
            _walkVelocityY = -Math.Abs(_walkVelocityY);
        }
    }
}
