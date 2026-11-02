using UnityEngine;

/// <summary>Shared pointer clock for native desktop and Editor hand-leading.</summary>
public sealed class PetHandLeadGesture
{
    public const float HoldSeconds = .28f;
    public bool Pending { get; private set; }
    public bool Active { get; private set; }
    public bool Running { get; private set; }
    public string Side { get; private set; }
    public float Velocity { get; private set; }
    public float VerticalVelocity { get; private set; }
    private float pressedAt, previousAt;
    private Vector2 previousPoint;
    public void Press(Vector2 point, float now, string side)
    {
        Cancel();
        if (side != "L" && side != "R") return;
        Pending = true; Side = side; previousPoint = point; pressedAt = previousAt = now;
    }
    public void Advance(Vector2 point, float now)
    {
        if (!Pending) return;
        float dt = Mathf.Clamp(now-previousAt, .001f, .1f);
        float instantaneous = Mathf.Clamp((point.x-previousPoint.x)/dt, -1400, 1400);
        Velocity = Mathf.Lerp(Velocity, instantaneous, 1-Mathf.Exp(-dt/.09f));
        VerticalVelocity = Mathf.Lerp(VerticalVelocity,Mathf.Clamp((point.y-previousPoint.y)/dt,-1400,1400),1-Mathf.Exp(-dt/.09f));
        previousPoint = point; previousAt = now;
        if (now-pressedAt >= HoldSeconds) Active = true;
        float speed = Mathf.Abs(Velocity);
        if (!Running && speed > 240) Running = true;
        else if (Running && speed < 160) Running = false;
        if (speed < 4) Velocity = 0;
    }
    public void Cancel() { Pending = Active = Running = false; Side = null; Velocity = VerticalVelocity = 0; }
}

public sealed class PetHandFallMotion
{
    public bool Active { get; private set; }
    public float Height { get; private set; }
    public float Velocity { get; private set; }
    public void Begin(float height) { Height=Mathf.Clamp(height,0,4000);Velocity=0;Active=Height>0; }
    public bool Step(float seconds)
    {
        if(!Active)return false;
        float dt=Mathf.Clamp(seconds,0,.1f);
        Height-=Velocity*dt+900f*dt*dt; Velocity+=1800f*dt;
        if(Height>0)return false;
        Height=0;Active=false;return true;
    }
    public void Cancel() {Active=false;Height=Velocity=0;}
}
