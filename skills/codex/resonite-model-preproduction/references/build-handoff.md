# Build Handoff Template

Read this entire template when drafting a handoff. Replace every placeholder; retain every section. For non-applicable fields write `not-applicable — reason`. For unknown facts write `unknown — question/owner`; for assumptions write `assumed — basis`. Separate proposed requirements from approved decisions and measured results. No production, capture or runtime check has been performed by preproduction.

## Contents

1. Goal and decision status
2. Asset Specification
3. Acceptance Definition
4. Production route and inputs
5. Paid-operation proposal and permissions
6. Model architecture and construction
7. Production phases and review gates
8. Handoff destination and outstanding decisions

## 1. Goal and decision status

- Asset / goal: <name and intended result>
- Required features: <must-have forms, details and behavior>
- Handoff revision / source brief: <revision and source>
- Handoff Approval Status: <draft/unapproved, or explicit approval evidence for this revision>
- Approved decisions: <decision, scope, evidence; none if not approved>
- Measurement / execution status: <unmeasured; production and validation pending>

## 2. Asset Specification

| Field | Requirement / evidence / status |
| --- | --- |
| Purpose | <use and hero/background/repeated role> |
| Visual Direction | <style, reference priorities and departures to avoid> |
| Physical Dimensions | <width/height/depth with units, orientation, origin and connection dimensions> |
| Dimensional Tolerance | <controlled dimensions, tolerances and planned measurement methods> |
| Silhouette | <recognizable outline from key views> |
| Major Forms | <primary masses and proportions> |
| Secondary Forms | <construction layers, transitions, fittings> |
| Focal Details | <features visible at required distances> |
| Materials | <part assignments and intended physical response> |
| Surface Condition | <wear, damage, grain, cleanliness and variation> |
| Color/Emission | <palette, color profiles, emissive areas and intensity intent> |
| Viewing Conditions | <normal/minimum distance, front/side/rear/45-degree views and lighting assumptions> |
| Interaction Requirements | <grasping, placement, controls and expected responses> |
| Moving Parts/Pivots | <static/moving, part separation, pivot positions/axes/ranges> |
| Runtime Considerations | <simultaneous count, colliders, sharing, shadows, interaction and resource rationale> |
| Editability | <editable parts, parameters and source retention> |
| Source Inputs | <paths/provenance, usage rights, formats and external-transmission restrictions> |
| Reference Images | <local references, provenance/rights, intended guidance and transmission constraints> |
| Non-Goals | <explicit exclusions> |
| Assumptions | <minor assumptions, basis and consequences if wrong> |

## 3. Acceptance Definition

For each criterion specify target, comparison/measurement method, view/distance or runtime condition, evidence owner and status (`planned — unmeasured` initially). Use the same criteria for native, authored, generated and adapted assets. Justify resources by role, distance and simultaneous count; do not add universal polygon/texture limits.

### Visual

- Front / side / rear / 45-degree views: <silhouette, proportion and major/focal features to compare>
- Distant / normal / minimum viewing distances: <what must remain readable at each>
- Visual direction and reference comparison: <style, construction detail and acceptable departures>
- Evidence: <later actual Resonite captures and review; pending>

### Geometry

- Dimensions / interface tolerances: <targets, units and measurement method>
- Major/secondary form proportions: <comparison method>
- Floating geometry / unwanted intersections / normals: <inspection criteria; intentional overlaps distinguished>
- Object/part separation / hierarchy / pivot axes and ranges: <structural checks>
- Evidence: <later dimensional and structural inspection; pending>

### Material

- Material assignment / surface response: <part-level criteria and lighting/view conditions>
- UVs / textures / channel packing / color profiles / normals: <orientation, seams and data-versus-color checks>
- Color / emission / wear: <comparison criteria>
- Evidence: <later material review in Resonite; pending>

### Runtime

- Expected simultaneous placements / resource sharing: <count, role-based resource review>
- Colliders / grasping / interaction / moving parts: <structural and manual behavior checks>
- Integration / ownership / portability: <bounded target, native responsibilities and save/reload checks when applicable>
- Resource evidence: <geometry, expanded vertices, meshes, materials, draw sections, texture dimensions/memory, lights/drivers; estimates are not measured frame cost>
- Evidence: <later runtime checks; pending, structural-only checks do not prove interaction>

## 4. Production route and inputs

- Selected route: <Resonite native / Blender authored / Meshy-assisted Blender / Hybrid / Existing asset adaptation>
- Selection reasons: <derived from specification and acceptance>
- Rejected alternatives: <route and reason>
- Input assets / usage rights / source formats: <verified facts or unresolved questions; retain acquired originals>
- External-transmission constraints: <exact allowed/prohibited inputs and destinations; unknown permission stays unknown>
- Meshy suitability: <base-generation role or exclusion; precision/mechanics/pivots/topology/UV constraints>
- Blender cleanup: <geometry reconstruction, topology, dimensions, pivots, UV/material correction>
- Native finishing: <geometry, colliders, Components, interaction and ProtoFlux responsibilities>
- Exporter compatibility: <execution owner checks current resonite-blender scope for rigs/animation/shape keys, transparency/transmission/special shaders and GLB source adaptation; plan intentional manual Blender adaptation, static export + native runtime where appropriate, or an alternate route; no unsupported transfer promised>
- Fallback: <trigger, alternate route, retained sources and impact on acceptance>

## 5. Paid-operation proposal and permissions

Paid Operation Approval Status: not-authorized-at-preproduction-stage

Handoff approval does not authorize spending or image transmission. A shareable reference, usage license, possession or available key does not authorize external transmission to a particular destination. An existing permission claim is context only; the execution owner must recheck the exact operation, inputs and scope. Keep unknown budgets, settings, submission counts and permissions proposed or `unknown`; block only affected paid/transmission execution, not drafting this handoff. Do not include credentials. A non-Meshy route still retains the status above and marks the proposal non-applicable with a reason.

- Meshy generation mode: <text-to-3D / image-to-3D; not-applicable — reason for a non-Meshy route>

| Proposed operation | Generation scope / inputs sent / destination | Submission count | Budget ceiling / currency or credits | Model | Texture / PBR / resolution | Permission evidence to recheck |
| --- | --- | --- | --- | --- | --- | --- |
| <operation; preview and refine counted as separate submissions> | <exact text/images/asset references; no secret contents> | <count per operation, total> | <ceiling, not inferred> | <proposed model> | <settings> | <not granted here; prior claim if supplied> |

- Image-transmission Approval Status: <not authorized here; constraints/prior evidence to recheck>
- Regeneration proposal: <additional paid operation; consider local refinement first>
- External skill availability: <meshy-resoloop present/unknown/absent; absent means installation or alternate route, no paid work>

## 6. Model architecture and construction

One mesh per object; keep independently moving parts as separate objects. Define semantic object boundaries, not a mesh for every decorative face.

| Object / part | Ownership / hierarchy / reuse | Mesh / materials | Static or moving / pivot axis and range | Collider / interaction owner | Construction method |
| --- | --- | --- | --- | --- | --- |
| <part> | <owned root/parent, reusable source> | <mesh and assignments> | <origin/pivot requirements> | <native/build responsibility> | <native/authored/generated/adapted> |

- Meshy generation boundary: <base shapes only; decide before generation>
- Retain / discard / rebuild in Blender: <regions and why; preserve accepted generated regions>
- Primary/secondary construction and cleanup: <dimensional corrections, topology, normals and detail>
- Texture/channel corrections: <UVs, baking, packing and profiles; verify target material conventions later>
- Native finishing and behavior: <colliders, Components, interaction, ProtoFlux; not implemented here>
- Editable source retention: <.blend, acquired originals, source textures/scripts and planned export locations>
- Local refinement policy: <keep satisfactory forms and approved proportions; whole rebuild only for structural problems>

## 7. Production phases and review gates

Every row needs a deliverable and an exit check. These are future build steps, not completed work.

| Phase | Planned deliverable | Exit check / review gate / evidence |
| --- | --- | --- |
| 0 — Inputs/route | <rights, constraints, chosen route and permissions to verify> | <resolve blocking input/permission/exporter questions before affected execution> |
| 1 — Blockout/base | <native/authored blockout or proposed generated candidates> | <coarse dimensions and major masses before detail> |
| 2 — Silhouette/proportion/candidate | <comparison and selected/rejected candidate rationale> | <Gate 1: silhouette/proportion; views and distances; reject unsuitable bases before cleanup/detail> |
| 3 — Primary forms/cleanup | <primary construction and base corrections> | <dimensional/topology/normal checks toward Gate 2> |
| 4 — Secondary detail | <secondary forms and focal detail> | <Gate 2: geometry/structure; measurements, part/pivot/hierarchy checks> |
| 5 — Materials/textures | <assigned materials, corrected UVs/textures> | <Gate 3: materials; response/channel/color/emission review> |
| 6 — Integration | <export and native integration under verified ownership> | <structural/runtime inspection toward Gate 4> |
| 7 — Visual validation | <actual Resonite front/side/rear/45-degree and distance captures> | <compare Visual/Geometry/Material/Runtime acceptance; measurements/manual checks> |
| 8 — Targeted refinement | <local fixes preserving satisfactory/approved/accepted regions> | <Gate 4: integrated visual/runtime acceptance; recheck affected views/behavior; outstanding checks explicit> |

- Review participants / approval evidence: <owner and required decisions per gate>
- Validation views / lighting / distances: <specific view plan and comparison references>
- Measurement plan: <controlled dimensions and structure/material/runtime evidence to collect>
- Structural rebuild trigger: <why local edits cannot resolve it; no blanket regeneration>

## 8. Handoff destination and outstanding decisions

- Execution owner: `resonite-build` first; consume this completed brief without repeating preproduction.
- Native / Blender path: <build → resonite-blender as needed → native integration/verification>
- Meshy path: <build rechecks explicit paid/transmission permissions and external meshy-resoloop → Blender finishing → native integration/verification; unavailable skill means stop paid work/report alternate route>
- Non-goals to preserve: <scope exclusions>
- Assumptions to recheck: <assumption, consequence and owner>
- Open questions / blockers: <question, affected step and decision owner>
- Outstanding approvals: <handoff decisions separate from paid operations/image transmission>
- Outstanding measurements: <actual captures, dimensions/structure, materials and runtime checks; do not mark performed>

End preproduction with this handoff and outstanding decisions. Do not attach production execution commands or start the build.
