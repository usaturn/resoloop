---
name: resonite-model-preproduction
description: "Use when a Resonite 3D asset needs concept exploration, a model brief, specifications, acceptance criteria, or production-route planning before building. Not for execution-only requests with a completed build handoff."
---

# Resonite Model Preproduction

## Boundaries

Produce a Build Handoff, then stop. Do not start Meshy API calls (including status, download, retry or recovery), conversion, Blender operations, world changes, apply, ProtoFlux implementation or capture. Do not request, display or store credentials.

A paid-operation proposal is not permission. Handoff approval and paid-operation/image-transmission approval are separate. A shareable reference, usage license or possession does not authorize transmission to a particular external destination; key availability is not permission either. Always record `Paid Operation Approval Status: not-authorized-at-preproduction-stage`; even previously supplied permission must be rechecked by the execution owner against the exact operation and inputs. Never infer privacy, transmission or spending permission.

Keep unknown budgets, settings, submission counts and permissions proposed or `unknown` in the handoff. They block only the affected paid/transmission execution, not drafting the handoff.

Do not apply unit tests/TDD to a 3D asset, its appearance or one-off modeling scripts. Use visual, dimensional, structural, material and runtime reviews. Reusable importers, exporters, add-ons, pipelines and libraries remain software and follow normal testing policy.

## Seven phases

### 1. Concept Exploration

Establish purpose, hero/background/repeated role, static/moving state, visual direction, normal/minimum viewing distance, grasping, placement count, source inputs, external-transmission restrictions, dimensional/interface accuracy and editability. Read supplied references without sending them elsewhere. State minor assumptions and proceed; ask only about consequential direction gaps. Keep unresolved permission questions explicit, not assumed.

### 2. Asset Specification

**Read [the complete handoff template](references/build-handoff.md) before drafting.** Fill every Asset Specification field before selecting tools. Specify units, dimensional tolerances and measurement methods for controlled dimensions. Separate major forms, secondary forms and focal details; record reference provenance, non-goals and assumptions. Use `not-applicable — reason` rather than blanks.

### 3. Acceptance Definition

Define comparable Visual, Geometry, Material and Runtime criteria in the template. Plan front, side, rear and 45-degree views at the stated distances; include silhouette/proportion, dimensions, floating/intersecting geometry, normals, textures/emission, part separation/pivots, colliders and interactions. Criteria apply to every route. Budget quality and runtime resources by role, viewing distance and simultaneous count; impose no universal polygon or texture-size ceiling.

### 4. Production Route Selection

Choose only after specification and acceptance:

| Route | Selection basis |
| --- | --- |
| Resonite native | Simple or runtime-parametric geometry; native materials and behavior suffice. |
| Blender authored | Controlled dimensions, mechanical construction, moving-part pivots, strict topology/UVs or no external transmission. |
| Meshy-assisted Blender | Reference-led organic, irregular or damaged base shapes; generated output still needs review and cleanup. |
| Hybrid | Authored/generated forms plus native geometry, colliders, Components or behavior. Identify each boundary. |
| Existing asset adaptation | Suitable source exists; verify usage rights, provenance, source format and suitability before planning changes. |

Record reasons, rejected alternatives, input rights/formats, transmission constraints, Meshy suitability, Blender cleanup, native finishing and fallback. Meshy is base generation only, not precision mechanics or runtime behavior. Prefer Blender for precision and moving parts. External-transmission prohibition excludes sending those inputs to Meshy. The execution owner must check the current `resonite-blender` exporter scope for rigs, animation, shape keys, transparency, transmission, special shaders and GLB source adaptation. Plan intentional manual Blender adaptation (static export plus native runtime behavior where appropriate) or an explicit alternate route; never promise unsupported transfer.

### 5. Model Architecture

Design one mesh per object, with separate objects for independently moving parts. Specify ownership, parts, materials, pivots, colliders, reuse and hierarchy. Before generation, distinguish Meshy scope and regions to retain, discard or rebuild in Blender; plan texture/channel corrections and native collider/Component/interaction/ProtoFlux responsibilities. Preserve editable `.blend` and acquired originals, not only exports.

### 6. Production Planning

Plan outcomes and exit checks for Phase 0 inputs/route, 1 blockout/base, 2 silhouette/proportion/candidate review, 3 primary forms/cleanup, 4 secondary detail, 5 materials/textures, 6 integration, 7 visual validation and 8 targeted refinement. For Meshy, explicitly select text-to-3D or image-to-3D. Text-to-3D: count preview and any proposed refine as separate paid operations/submissions (one each). Image-to-3D: count each actual proposed image-generation operation/submission (one each); do not automatically add preview/refine stages. Itemize submission counts and budget ceilings for the actual proposed operations. Blockout first; review generated candidates against acceptance before committing to cleanup or detail. Reject unsuitable bases rather than hiding defects with textures.

Use four review gates: **silhouette/proportion** after Phase 2; **geometry/structure** after Phases 3–4; **materials** after Phase 5; **integrated visual/runtime acceptance** after Phases 6–8. Specify views, measurements and evidence at each gate. Actual Resonite capture, dimension/structure inspection, material review and runtime checks belong to the later build; mark them unmeasured/pending here.

Refine locally: preserve satisfactory forms, approved proportions and accepted generated regions. Rebuild the whole only for structural problems. Consider local edits before Meshy regeneration; regeneration is another paid operation requiring separate permission.

### 7. Build Handoff

Complete every section of the template, including required features, construction/cleanup/native finishing, phase deliverables/gates/views, runtime considerations, fallback and open questions. Distinguish proposed, assumed, approved and measured facts; do not call a draft approved or a planned check performed.

Pass it first to `resonite-build`. Native/Blender work proceeds through build, with `resonite-blender` as needed. For Meshy, build must confirm separate explicit permission and the bundled `meshy-resoloop` skill and its optional local dependencies, then Blender finishing and native integration/verification. `init`/`skills sync` deploy its workflow and scripts, not Node/Python/Blender or credentials. If absent/outdated, report reviewed skill sync, optional dependency setup or an alternate route without starting paid work. Do not duplicate its wrappers, secret handling, journal or conversion workflow. End with the handoff and outstanding decisions, not execution commands.
