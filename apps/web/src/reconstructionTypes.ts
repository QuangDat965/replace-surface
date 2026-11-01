export type ImagePoint = { x: number; y: number }
export type ImagePolygon = { points: ImagePoint[] }
export type ReconstructionAssumptions = { cameraModel: string; scaleStatus: string; metersPerPixel: number | null; notes: string[]; referencePixelLength: number | null; referenceLengthMeters: number | null }
export type DetectedSurface = { id: string; semanticRole: string; polygon: ImagePolygon; estimatedWidthMeters: number | null; estimatedHeightMeters: number | null; confidence: number; provenance: string; linkedSurfaceId: string | null }
export type DetectedObject = { id: string; type: string; name: string; polygon: ImagePolygon; positionMeters: [number, number, number]; dimensionsMeters: [number, number, number]; confidence: number; provenance: string; included: boolean; linkedObjectId: string | null }
export type SceneAnalysisResult = { contractVersion: number; analysisId: string; sourceImageId: string; imageWidth: number; imageHeight: number; providerId: string; providerVersion: string; createdAt: string; assumptions: ReconstructionAssumptions; surfaces: DetectedSurface[]; objects: DetectedObject[] }
