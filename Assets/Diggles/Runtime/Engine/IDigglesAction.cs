#nullable enable

namespace Diggles.Runtime
{
    // Mirror of the original engine's CAction base class. Each subclass
    // represents one of the high-level behaviours a CObj can be in (walk,
    // mine, build, eat, sleep, etc.). Tcl scripts trigger actions via the
    // `action this <name> -args` command; in our port the Tcl runtime
    // (when embedded) will instantiate the right IDigglesAction and call
    // DigglesObject.SetAction.
    //
    // Lifecycle:
    // Start(obj) — once, on attach. Initialise state, set initial
    // anim, etc.
    // Tick(obj, dt) — every frame. Returns false when the action is
    // finished (the object then clears its current
    // action and may pick another).
    // End(obj) — once, on detach. Reset transient state (zero
    // velocity, switch to idle anim).
    public interface IDigglesAction
    {
        void Start(DigglesObject obj);
        bool Tick(DigglesObject obj, float deltaTime);
        void End(DigglesObject obj);
    }
}
