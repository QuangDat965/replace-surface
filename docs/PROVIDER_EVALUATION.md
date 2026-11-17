# External AI provider evaluation (rechecked 2026-11-01)

This is a documentation-based shortlist, not approval to send customer data. Recheck these pages and account-level pricing before any live call.

## Phase 05: image segmentation

| Candidate | Fit | Input/output and execution | Price observed | Data / rights notes |
|---|---|---|---|---|
| fal `fal-ai/sam-3/image` | Strong for promptable object masks (text, point, box, mask); does not itself produce room layout, metric depth, or calibrated dimensions | Required `image_url`; data URI or hosted URL accepted; queue submit/status/result; returns mask image URLs, optional scores and normalized boxes. API key is `FAL_KEY`. | $0.005/request on model page, rechecked 2026-11-01 | Model page labels commercial use. fal DPA says it applies when fal processes Client Personal Data as processor and retains such data until agreement termination; deletion is available at termination. API Services terms say inputs to third-party models are transferred to those parties and exclude “Pending Enterprise Ready” models from the no-training and DPA protections. The public SAM 3 page does not establish whether this model is a third-party API or excluded model, nor does it establish the authenticated account's DPA coverage. Use only public/licensed non-sensitive photos for any smoke test; do not send customer/private images until those account/model details are confirmed. |
| fal `fal-ai/sam-3/image-rle` | Same segmentation use case with compact mask representation, useful for browser polygon conversion | Async queue; inspect current schema before implementing; API key via fal | $0.005/request on model page | Same fal privacy/retention caveat. |

**Selection for a bounded prototype:** fal SAM 3 image, only as a segmentation adapter behind the human-reviewed annotation flow. It is not a room-understanding provider and will not be treated as one. Keep the manual editor as fallback. Before live data: use licensed synthetic/interior images only, and obtain provider credential in User Secrets/environment. No credential is currently confirmed in this workspace.

## Phase 06: image-to-3D

| Candidate | Fit | Output | Price observed | Data / rights notes |
|---|---|---|---|---|
| fal `fal-ai/hunyuan3d-v3/image-to-3d` | Image-to-3D asset for a user-selected crop; provider page describes high-resolution mesh/PBR | Async API; response includes GLB (`model/gltf-binary`) URL and size; official example is ~64 MB, so application cap is 80 MB and parser validation is mandatory | Rechecked 2026-11-01: Geometry is $0.225; custom face count, PBR, multi-view each add $0.15. The adapter omits those options to keep the documented base price; future reservation should be $0.50+ margin per generation and re-check account pricing. | Page labels commercial use. fal general retention and this model/account DPA coverage remain unconfirmed; no private imagery is allowed for live smoke. Provider output is not assumed dimensionally accurate; preserve primitive fallback. |

**Selection for evaluation:** fal Hunyuan3D v3, Geometry-only, no optional paid settings. Proceed with fixtures and adapter contract tests first. Before a live call, require verified pricing in the authenticated account, credential, and confirmed image rights/retention.

## Shared live-test budget and gates

- Maximum combined spend for Phase 05–06 live smoke tests: **$20 USD**.
- At the rechecked listed rates, three SAM 3 requests plus three Geometry-only Hunyuan generations are about **$0.69 USD**, excluding taxes, retries, transfer, or changed pricing. The user-approved total cap is **$20 USD**; implement a per-run estimate guard before enabling any live calls. The budget is a hard cap, not a target.
- No API call is authorized by this estimate alone. No secret was found or printed. Run fixture/mock tests first; add account spend limit and explicit per-job estimate enforcement before enabling live providers.
- Privacy/retention is not established for room/customer images by the general public policy. Use only synthetic/licensed test images after confirming applicable terms; otherwise mark live smoke blocked.

## Official sources

- [fal SAM 3 Image model and price](https://fal.ai/models/fal-ai/sam-3/image)
- [fal SAM 3 Image API, schema, auth and queue](https://fal.ai/models/fal-ai/sam-3/image/api)
- [fal queue submit REST contract](https://fal.ai/docs/api-reference/inference-api/queue/submit)
- [fal queue status REST contract](https://fal.ai/docs/api-reference/inference-api/queue/status)
- [fal queue result REST contract](https://fal.ai/docs/api-reference/inference-api/queue/result)
- [fal SAM 3 RLE API](https://fal.ai/models/fal-ai/sam-3/image-rle/api)
- [fal Hunyuan3D v3 Image-to-3D API and pricing](https://fal.ai/models/fal-ai/hunyuan3d-v3/image-to-3d)
- [fal Privacy Policy](https://fal.ai/legal/privacy-policy) (last updated 2026-07-22)
- [fal Data Processing Addendum](https://fal.ai/legal/data-processing-addendum) (last updated 2026-07-31)
- [fal API Services Terms](https://fal.ai/legal/api-services) (third-party model data transfer and excluded-model clauses)
