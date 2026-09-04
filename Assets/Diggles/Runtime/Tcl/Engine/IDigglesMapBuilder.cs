#nullable enable

namespace Diggles.Runtime.Tcl.Engine
{
    // Bridge: when Tcl runs a campaign script, the engine forwards
    // map-construction commands (`map create`, `map_template`,
    // `map_setlayer2`) to a host-provided builder. The builder owns
    // file resolution (resolves `hol_024.pmp` against the actual
    // `Templates/pmp/` subdir) and the eventual Unity geometry emission.
    //
    // The bare interface is enough for tests (mock builder records calls).
    // The Editor assembly implements it via EditorMapBuilder which routes
    // into the existing BdbPmpWorldBuilder infrastructure.
    //
    // `split_template` is not here — it's handled inside the engine's Tcl
    // command itself (loop over chunks, source sub-tcl, call MapTemplateSet).
    // The builder only sees the per-chunk LoadPmp / LoadL2m calls that
    // result.
    public interface IDigglesMapBuilder
    {
        // Initialize the world array. Called by `map create W H` (also
        // surfaces in some Diggles scripts as `sm_create_map W H`).
        void CreateMap(int width, int height);

        // Apply a PMP (block_idx + heightmap + zone + terrain_type
        // streams) at world position (worldX, worldY). `pmpRelPath` is
        // the script-provided filename; builder resolves it against
        // its data root.
        void LoadPmp(string pmpRelPath, int worldX, int worldY);

        // Overlay an L2M (cell-id material/animation grid) at world
        // position. Diggles `map_setlayer2` writes on top of whatever
        // `map_template` already put there.
        void LoadL2m(string l2mRelPath, int worldX, int worldY);
    }
}
