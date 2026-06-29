1. Image Document (1–15)
STT	Function
1	CreateImage
2	OpenImage
3	SaveImage
4	SaveImageAs
5	CloseImage
6	CloneImage
7	ResizeCanvas
8	CropCanvas
9	RotateCanvas
10	FlipCanvas
11	GetImageInfo
12	SetImageMetadata
13	OptimizeImage
14	CompressImage
15	ConvertImageFormat
2. Resize & Transform (16–30)
STT	Function
16	ResizeImage
17	ScaleImage
18	CropImage
19	RotateImage
20	FlipHorizontal
21	FlipVertical
22	SkewImage
23	PerspectiveTransform
24	ShearImage
25	PadImage
26	TrimTransparentBorders
27	AutoCrop
28	AutoRotate
29	DeskewImage
30	StraightenImage
3. Color (31–50)
STT	Function
31	AdjustBrightness
32	AdjustContrast
33	AdjustGamma
34	AdjustExposure
35	AdjustSaturation
36	AdjustHue
37	AdjustTemperature
38	AdjustTint
39	AdjustVibrance
40	ConvertToGrayscale
41	ConvertToBlackWhite
42	InvertColors
43	SepiaEffect
44	PosterizeImage
45	ThresholdImage
46	EqualizeHistogram
47	AutoLevels
48	AutoContrast
49	ReplaceColor
50	MakeTransparent
4. Filters (51–70)
STT	Function
51	BlurImage
52	GaussianBlur
53	MedianBlur
54	MotionBlur
55	SharpenImage
56	EmbossImage
57	EdgeDetection
58	OilPaintingEffect
59	PencilSketchEffect
60	CartoonEffect
61	PixelateImage
62	MosaicEffect
63	NoiseReduction
64	AddNoise
65	VignetteEffect
66	GlowEffect
67	ShadowEffect
68	BloomEffect
69	LensBlur
70	CustomConvolution
5. Drawing (71–90)
STT	Function
71	DrawLine
72	DrawRectangle
73	DrawRoundedRectangle
74	DrawCircle
75	DrawEllipse
76	DrawPolygon
77	DrawBezierCurve
78	DrawArrow
79	DrawText
80	DrawWatermark
81	DrawImage
82	DrawQRCode
83	DrawBarcode
84	FillRectangle
85	FillCircle
86	DrawGrid
87	DrawBorder
88	DrawShadow
89	DrawGradient
90	DrawPattern
6. Text (91–100)
STT	Function
91	AddText
92	ReplaceText
93	RemoveText
94	MeasureText
95	SetFont
96	SetFontSize
97	SetFontColor
98	SetTextAlignment
99	RotateText
100	WarpText
7. Layers (101–115)
STT	Function
101	CreateLayer
102	DeleteLayer
103	MergeLayers
104	DuplicateLayer
105	MoveLayer
106	RenameLayer
107	HideLayer
108	ShowLayer
109	LockLayer
110	UnlockLayer
111	SetLayerOpacity
112	BlendLayers
113	FlattenLayers
114	ReorderLayers
115	GroupLayers
8. Selection (116–125)
STT	Function
116	SelectRectangle
117	SelectEllipse
118	SelectPolygon
119	SelectByColor
120	InvertSelection
121	ExpandSelection
122	ContractSelection
123	FeatherSelection
124	ClearSelection
125	CropSelection
9. Metadata (126–135)
STT	Function
126	GetEXIF
127	UpdateEXIF
128	RemoveEXIF
129	GetIPTC
130	UpdateIPTC
131	RemoveMetadata
132	SetCopyright
133	SetAuthor
134	SetGPSLocation
135	StripMetadata
10. Export (136–150)
STT	Function
136	ExportPNG
137	ExportJPEG
138	ExportBMP
139	ExportGIF
140	ExportTIFF
141	ExportWEBP
142	ExportSVG
143	ExportPDF
144	ExportPSD
145	ExportICO
146	GenerateThumbnail
147	CreateSpriteSheet
148	SplitSpriteSheet
149	OptimizeForWeb
150	GeneratePreview
Nếu làm AI Image Editor chuyên nghiệp (250–300 tool)
Background
RemoveBackground
ReplaceBackground
BlurBackground
ExtendBackground
FillBackground
TransparentBackground
Face
DetectFaces
BlurFaces
CropFace
AlignFace
EnhancePortrait
Objects
DetectObjects
RemoveObject
ReplaceObject
CountObjects
OCR
DetectText
ExtractText
TranslateText
RemoveText
RedactText
Batch Processing
BatchResize
BatchWatermark
BatchConvert
BatchCompress
BatchRename
AI Enhancement
SuperResolution
DenoiseImage
DeblurImage
RestoreOldPhoto
ColorizePhoto
EnhanceLowLight
UpscaleImage
FaceRestore
ScratchRemoval
ArtifactRemoval
Canvas
ExpandCanvas
AddMargin
SetCanvasColor
CenterImage
FitImageToCanvas
Composition
OverlayImage
BlendImages
CreateCollage
CreateContactSheet
TileImage
Analysis
DetectEdges
DetectContours
DetectDominantColors
CalculateHistogram
MeasureObject
DetectTransparency
Kiến trúc Tool Calling đề xuất

Tương tự Word, PDF và Excel, bạn có thể chia các tool thành ba cấp:

Low-level tools (~120): thao tác nguyên tử như CropImage, AdjustBrightness, DrawText, BlurImage.
Medium-level tools (~50): kết hợp nhiều thao tác, ví dụ CreateThumbnail, GenerateSocialMediaBanner, OptimizeForWeb.
High-level tools (~20): thực hiện quy trình hoàn chỉnh như GenerateProductImage, PreparePassportPhoto, CreatePhotoCollage, EnhancePortrait, RestoreOldPhoto.