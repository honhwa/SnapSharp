# NimCap Screenshot User Guide: Screenshots, Scrolling Capture, OCR, Translation, Background Removal, and Screen Recording

[简体中文](使用指南.md) | **English**

Repositories: [Primary GitHub repository](https://github.com/nimcap/nimcap-screenshot) | [Gitee mirror (legacy name)](https://gitee.com/lilongjiang/SnapSharp)

NimCap Screenshot was formerly called SnapSharp; the Gitee mirror retains the legacy repository name. Submit bugs and feature requests to the primary repository's [GitHub Issues](https://github.com/nimcap/nimcap-screenshot/issues). Search both names for historical reports and check the affected version.

NimCap Screenshot is a Windows screenshot and image-processing tool. In addition to capturing screenshots, drawing arrows, and applying pixelation, it can pin images above other windows, stitch scrolling screenshots, recognize text in images, translate text, remove image backgrounds, and record MP4 or GIF files.

After launch, NimCap Screenshot stays in the system tray and does not open a main window by default. Start common tasks with a keyboard shortcut or right-click the tray icon to choose a feature.

## Take Your First Screenshot in One Minute

1. Launch `NimCap.Screenshot.exe` and find its icon in the notification area. Expand the hidden icons if necessary.
2. Press `Ctrl+Shift+A`, then drag to select an area.
3. Choose an arrow, text, or pixelation tool from the toolbar beside the selection and annotate the screenshot directly.
4. Double-click the selected area or click Copy, then paste the result into a chat or editor. You can also save it or pin it above other windows.
5. Press `Esc` to cancel the capture.

Capturing, annotating, and saving screenshots do not require an AI model. OCR, background removal, and local translation require their respective models to be configured before first use. Once downloaded, the models run locally.

<a id="start"></a>


## 1. Starting, Exiting, and Keyboard Shortcuts

### System requirements

NimCap Screenshot runs on Windows 10 version 2004 or later and Windows 11. It is currently available for x64 systems.

- The self-contained release does not require a separate .NET Desktop Runtime. Keep the entire application directory instead of copying only the EXE.
- The framework-dependent release requires the .NET 10 Desktop Runtime.
- Native features such as screen recording may require the VC++ x64 runtime and Windows media components.
- An internet connection is required for the initial model download. Local screenshots, OCR, background removal, and local translation do not upload your content.

It is normal for no window to appear after you double-click the application. Right-click the tray icon to access screenshots, clipboard image recognition, scrolling capture, screen recording, pinned images, screenshot history, and settings.

Tray menus, window menus, and pinned-image context menus include icons for their commands. An arrow indicates a submenu, a check mark indicates an enabled or selected state, and a gray item is temporarily unavailable.

### Default shortcuts

| Shortcut | Action |
|---|---|
| `Ctrl+Shift+A` | Capture an area |
| `Ctrl+Shift+F` | Capture the full screen |
| `Ctrl+Shift+W` | Capture the current window |
| `Ctrl+Shift+V` | Create a pinned image from the clipboard |
| `Ctrl+Shift+H` | Hide/show all pinned images |

You can change these shortcuts under **Settings → Shortcuts**. If a shortcut conflicts with another application, NimCap Screenshot displays a warning. Choose an unused combination and save again. Optional shortcuts, such as restoring the most recently closed pin or restoring mouse interaction, may be left blank.

Every tray action can have a shortcut. Use each row's **In tray** checkbox to show or hide its menu entry; hidden actions keep their shortcuts. Empty submenus hide automatically. **Settings** and **Exit** are always visible and are not shortcut rows.

The tray menu also provides 3-, 5-, and 10-second delayed captures for menus, tooltips, and other interfaces that must be opened first. You can cancel during the countdown.

To close NimCap Screenshot, right-click the tray icon and choose **Exit**. If a recording is in progress, stop it and wait for the file to finish saving instead of terminating the process. You can enable launch at Windows sign-in from Settings.

<a id="selection"></a>

## 2. Annotating a Screenshot Directly

Area capture supports rectangular, elliptical, freehand lasso, and polygon selections. After selecting an area, you can edit the screenshot directly without opening a separate editor.

The toolbar has two levels. The main toolbar selects tools and output actions, while the options toolbar controls color, thickness, line style, fill, font, and similar settings. Some tools are grouped in drop-down menus; each group's main button reuses the tool most recently selected from that group.

Available tools include rectangles, ellipses, arrows, pens, highlighters, text, speech bubbles, numbered markers, watermarks, pixelation, blur, spotlights, and magnifiers. Existing annotations can be selected, moved, resized, rotated, reordered, undone, and redone.

<p align="center">
  <img src="../Screenshot/1.png" alt="NimCap Screenshot capture selection and annotation toolbars" width="900">
</p>
<p align="center"><em>The selection, size indicator, annotation tools, and output actions share one overlay.</em></p>

<p align="center">
  <img src="../Screenshot/7.png" alt="Screenshot annotations created with NimCap Screenshot" width="900">
</p>
<p align="center"><em>Rectangles, ellipses, arrows, text, pens, highlights, and other annotations can be combined.</em></p>

If the selection is incorrect, right-click to discard the current selection and annotations and select again from the same desktop snapshot. Press `Esc` to cancel the entire capture.

Copying or saving produces the final composited image, not a screenshot of the toolbar or preview interface. Annotation templates store only annotation objects and styles, not the underlying screenshot.

### Select a button, input, or other element automatically

Start a capture with `Ctrl+Shift+A`. It opens in **Element** mode by default. Hover over a recognizable control to highlight its bounds and display its type, name, and dimensions. Press `Tab` or use the capture-mode selector to change modes.

- Click to lock the element, then annotate, copy, save, or pin its cropped image.
- Scroll down to expand to a parent container, window content, or the entire window; scroll up to select a smaller child.
- Drag to select a region manually. `Tab` cycles through **Region → Element → Window → Full screen**.
- If detection is unavailable or slow, window selection and manual selection remain available. Right-click after selecting to start again, or press `Esc` to cancel.

No browser extension is needed. Page buttons, links, inputs, and other elements exposed through the browser's accessibility interface can also be selected by hovering. Coverage depends on the browser and page. CSS selectors, full DOM inspection, and screenshots of entire offscreen elements are not supported. Custom-drawn controls, canvases, and content without accessibility support may expose only their containing region.

The image is cropped from the desktop snapshot taken when capture begins, retaining backgrounds and overlapping content inside the element. Element and Window modes make pixels outside system-rounded corners or an application's native window region transparent, excluding desktop content outside dialog corners. PNG preserves this transparency. Rectangular child controls retain their shape, and manual Region captures do not inherit the window silhouette. Custom per-pixel transparent windows without a native region and translucency inside a window are not automatically reconstructed. Elements spanning monitors are cropped to the current monitor. Restart capture after moving the target window to refresh both the image and its bounds.

### Pick a color from the screen

Before you begin selecting an area, the magnifier beside the pointer displays a pixel grid, screen coordinates, and color values.

- Press `Shift` to cycle through HEX, RGB, RGBA, HSL, HSV/HSB, CMYK, and color names.
- Press `C` to copy the color value in the current format and exit without creating an image.
- Each new capture starts in HEX mode. While dragging a selection, `Shift` locks the aspect ratio instead of changing the color format.

A screen snapshot cannot recover a window's original transparency, so the RGBA alpha value is 1. Color names may be approximate, and CMYK values are conversions from RGB rather than color-managed print results.

### Sensitive-text blurring

Use the capture or pin-editing toolbar action to blur sensitive text. Configured local OCR and rules locate phone numbers, identity/card numbers, email, license plates, name fields, and addresses. A configured local Qwen translation model can assist with unlabeled names and addresses; candidates must match OCR text.

Detection quality depends on OCR model size and capabilities and on image clarity. When Qwen assistance is enabled, the language model's size and capabilities also matter. Larger models in the same family generally handle complex content better; choose compatible models that fit your hardware. The result is applied to image pixels and retained when copied or saved. Undo/redo covers the operation. Compare the treated regions with the source and add manual blur as needed before sharing.

### Standalone screen ruler

Open **Screen ruler** from the tray menu, or assign its global shortcut under **Settings → Keyboard shortcuts**. The shortcut is initially empty; clear it to disable it. Duplicate shortcuts are rejected. Drag the toolbar background to move. Drag any of the four blue edge handles or an entire edge to resize; the lower-right corner resizes both dimensions. Arrow keys move the window by one physical pixel. The blue intersection inside the scale bands is the base `(0, 0)`; measurement coordinates exclude the scale bands. In Box mode, internal scales start at the box's top-left zero, its size appears beside the box, and magnifier coordinates are relative to it. Hover over the toolbar title for coordinates and dimensions.

| Tool | Interaction and purpose |
| --- | --- |
| Linear `1` | Drag or click two points for length; any orientation, with `Shift` to constrain horizontally or vertically |
| Box `2` | Drag a rectangle for live `W × H` readings |
| Two points `3` | Read endpoint coordinates, `Δx`, `Δy` and diagonal distance |
| Guides `G` | Click or press `G` to create a centered horizontal and vertical guide on first use, then hide/show them. Pull more guides from the top/left scales; drag to move, right-click to delete |
| Angle `A` | Select on the toolbar, then click the vertex and a point on each ray; disabled initially |

All tools and their shortcuts are visible on the top toolbar, which wraps in narrow windows. Each tool retains one result, and results can be displayed together. Click a tool to make it active; clicking it again keeps it active. The small eye on the right of each tool button controls result visibility. Readings appear beside measurements; the footer has one line of instructions with full details on hover. Angle measurements show one step at a time. Hold `Alt` to snap to nearby screen edges, guides or measurement-area boundaries. Hold `Ctrl` to measure on a guide without moving it. Guides highlight and display their coordinate on hover, with a directional drag cursor. The magnifier starts enabled and displays pixels and HEX color; toggle it with `M`. Its position automatically avoids the pointer and measurement endpoints where space permits. A message appears if screen sampling is unavailable.

The magnifier uses a black-and-white outlined crosshair and pixel frame for visibility on white and black backgrounds. The reticle follows the actual sampled pixel, including off-center positions near desktop edges. Its center remains clear to preserve the sampled color.

Readouts prefer positions outside their measurement bounds and avoid measurement lines, angle arcs, endpoints, other readouts and the magnifier. Labels switch sides near window edges and search available space when crowded.

The toolbar's **Auto copy** toggle defaults off and remembers its state. When enabled, only completed measurements are copied: length for Linear, `width × height px` for Box, `Δx`, `Δy` and diagonal distance for Two points, or degrees for Angle. Press `Ctrl+C` to copy the active tool's completed result manually. Drafts, cancellation, guide movement and window resizing do not trigger copying. A brief footer message confirms success; if the clipboard is busy, press `Ctrl+C` to retry.

`Delete` removes a selected guide or the active tool's result. Right-click or `Esc` cancels an unfinished measurement; `Esc` closes the ruler when no measurement is pending. The trash button clears all measurements and guides. These tools do not change the full-screen capture ruler.

<a id="output"></a>

## 3. Copying, Saving, and Image Formats

Screenshots, pinned images, history, and the editor share the same Save window. Before saving, you can change the pixel dimensions, preserve the aspect ratio, and review the actual encoding result.

| Format | Recommended use |
|---|---|
| PNG | Text, interface screenshots, and transparent cutouts; the preferred default |
| JPEG | Photos or images that do not need transparency; lossy, with transparent areas filled white |
| WebP | Smaller files; supports both lossy and lossless modes |
| TIFF | Lossless output with 300 DPI metadata, which does not add pixel detail |
| BMP | Usually large files; transparent areas are filled white |
| PDF | A4 paginated output; long images are split automatically while the complete image remains visible in the Save preview |

WebP quality 100 is not the same as lossless mode. Explicitly select the lossless option when required. AVIF encoding is not currently available.

After saving, you can copy the file, copy its path, reveal it in File Explorer, or print it. Replacing an existing file requires confirmation. Image output is limited to 16,384 pixels per side and 64 million pixels in total.

### Automatic saving and history

Automatic saving and screenshot history are separate settings. Automatic saving is disabled by default. You can choose a destination, organize files by date, and define a filename template such as:

```text
NimCap Screenshot-{date}-{time}-{seq}
```

Templates support `{date}`, `{time}`, `{width}`, `{height}`, and `{seq}`. A suffix is added when an automatically generated name conflicts with an existing file. Date-based folders apply only to automatic saving, not Save As.

<a id="pins"></a>

## 4. Pinning Images or Text to the Desktop

Pins keep images, text, or colors above other windows, which is useful when working while referring to other material.

Copy an image, text, or a color value and press `Ctrl+Shift+V`, or click the single **Pins** entry in the tray. Clipboard colors (`#RGB`, `#RRGGBB`, `#AARRGGBB`, `rgb(...)`, `rgba(...)`, or `0xRRGGBB`) create color pins directly. You can also choose Pin from the capture toolbar.

| Action | Result |
|---|---|
| Mouse wheel | Scale the pin from 10% to 1000% |
| `Ctrl+mouse wheel` | Set window opacity from 20% to 100% |
| Right-click | Edit, copy, save, rotate, enable click-through, group, recognize, or remove the background |
| `Ctrl+Shift+H` | Hide/show all pins |

Scaling and window opacity do not change the saved image's actual pixels. Rotation is included when the result is copied or saved.

Configure a text pin's font, width, foreground color, and background color under **Settings → Pins**. A color pin can copy its HEX or RGB value. Pin groups last only for the current session; moving one pin in a group moves the entire group.

When click-through is enabled, mouse input goes directly to the window behind a pin. Configure a shortcut for **Disable Click-Through for Pins**, or enable its tray entry under **Settings → Keyboard Shortcuts**. The same applies to **Restore Last Closed Pin**. The tray has a single pin creation entry by default.

<a id="scrolling"></a>

## 5. Capturing Long Web Pages, Lists, and Chats

### Standard manual scrolling capture

1. Right-click the tray icon and choose **Scrolling Capture → Select Area and Start Scrolling Capture…**. You can also click **Scrolling Capture** on the normal area-capture toolbar.
2. Select the content, adjust the edges, then click **Start Scrolling Capture** or press Enter.
3. Place the pointer inside the area and scroll down slowly. Scrolling capture supports vertical scrolling only.
4. During capture, NimCap Screenshot displays a blue area border and eight circular anchors at the corners and edge midpoints. The border is click-through and does not block clicks or scrolling inside the area. Keep the window and selection in place; you can scroll up to revisit captured content.
5. When finished, click `√` at the upper-right of the area to open the preview. Click `×` or press `Esc` to discard the capture.
6. Inspect the seams in the preview, trim edges if needed, then copy, save, pin, or annotate the result.

Manual mode listens to your scrolling but does not generate wheel input. Reaching the end of a page or pausing does not finish the capture automatically. Click `√`, or use **Stop Current Capture and Preview** from the tray menu.

The anchors mark a fixed area and cannot be dragged during capture. To change the area, stop and select it again; changing coordinates while collecting frames can cause misalignment. Automatic capture uses the same area border. The border is removed after completion, cancellation, or failure and is not included in the final image.

If a frame cannot be matched reliably, capture stops and preserves both originals. The preview opens the seam correction panel. The unresolved frame does not extend the output until corrected: compare the originals and enter the correct nonzero offset, or exclude fixed bars and rematch. If the frames have no overlap, recapture more slowly; manual correction cannot recover missing content.

### Trimming, correcting, and recapturing

In the preview, select the top, bottom, left, or right edge, then click or drag on the image to define the part to remove. Trimming starts at an edge and cannot remove a section from the middle. You can trim repeatedly or restore the complete capture.

Expand seam correction to inspect source frames, fine-tune offsets, or exclude fixed bars from each frame before rematching. Trimming and seam adjustments support up to 20 undo/redo steps. Diagnostic lines and red warnings are not included in the output.

Use **Recapture (Manual/Automatic)** on the right to select a new area. Automatic mode generates wheel input; manual mode only listens to actual input. Canceling a recapture keeps the current preview.

Save a `.ssscroll` stitching project to continue editing later. A project contains the source frames and adjustment parameters, not only the final long image. Check captured content for private information before sharing it.

Each scrolling-capture frame is limited to 16 million pixels, with a maximum of 48 million pixels across source frames. Output is limited to 16,384 pixels per side and 64 million pixels in total. Capture extremely long content in sections. Do not move the target window or change display scaling during capture.

<a id="recognition"></a>

## 6. Image Tools for Recognition, Background Removal, and Translation

Open **Image Tools** from the capture toolbar or a pin's context menu.

<p align="center">
  <img src="../Screenshot/2.png" alt="NimCap Screenshot OCR, barcode, table, formula, and background-removal modes" width="900">
</p>
<p align="center"><em>Use one window for text OCR, identity documents, barcodes, tables, formulas, background removal, and watermark removal.</em></p>

1. Copy image content from a browser, chat application, or another program. Copy the image itself, not only its URL or file path.
2. Choose **Pins** from the tray, or press the default shortcut `Ctrl+Shift+V`.
3. Right-click the pin, choose **Image Tools**, and select a mode on the left.
4. Select a model if needed, then click **Start**, **Remove Background**, or **Remove Watermark**.

Alternatively, take a screenshot and click the scan-text or scissors icon on the selection toolbar. Recognition and background removal are also available from a pin's context menu, the editor, and the scrolling-capture result window.

Translation operates on text; it does not directly turn a portrait or ordinary image into translated content. Run OCR on text in an image before translating it.

### Watermark removal and image repair

The recognition window has a shared model/device row. OCR and identity documents can switch between PP-OCRv6 Tiny, Small, Medium, Windows OCR, and Tesseract. Table and formula recognition expose their local model and configured plugin; background removal lists models compatible with the selected device. Selecting a missing model offers its download information and a choice of source. A successful download configures paths and saves the default automatically. Switching between installed models applies to the current window. Windows OCR and Tesseract use CPU; plugins manage their own devices.

Watermark removal and background removal show the original on the left and the result on the right, with linked zoom/pan and undo for processing results. Image tools and output actions share one row below the previews. The top toolbar combines model, device and the settings gear; watermark mode places its action beside the mask controls below the images. Text-based pages have a draggable divider between the preview and results. Barcode scanning does not show model controls.

The window opens without starting recognition. Choose **Remove watermark** and select MI-GAN (fast), LaMa (default), or Moebius (detail candidate). Missing models show their purpose and download size on the current page, with official and China sources. Confirm to download, verify, configure paths and save the default. SLBR for region and smart detection installs through the same flow, without Python or manual conversion. Paint with the cursor-sized brush or mark a region with marching ants; region and smart modes hide brush and threshold controls. The mode and **Remove watermark** button share the toolbar below the images. One click detects and removes watermarks in region or smart mode. The original stays on the left and results appear on the right, with linked zoom and pan, undo, copy, save and pin actions. See [model contracts and limits](去水印与图像修复.md).

### Configure OCR for the first time

Recognition quality depends on model size and capabilities. Within the same family and task, with comparable training and quantization, larger models generally help improve accuracy; lightweight models use fewer resources and respond faster. Training data, language coverage, and image clarity also affect results. Choose among supported compatible models for your task and hardware, rather than comparing download sizes alone. OCR, tables, formulas, and local translation use separate models; upgrading the translation model does not change OCR results.

1. Open **Settings → OCR and Recognition** from the tray.
2. Keep the default PaddleOCR engine and start with CPU processing.
3. Under **PaddleOCR Models and Dictionaries**, select **PP-OCRv4 · Chinese + English**.
4. Click **Configure Recommended ONNX OCR (Download Model)** and wait for the approximately 14.2 MB package to download and pass verification.
5. Confirm that the model and dictionary paths were filled in, then click **Save Settings**.

<p align="center">
  <img src="../Screenshot/8.png" alt="NimCap Screenshot OCR and recognition settings" width="900">
</p>
<p align="center"><em>Configure the OCR engine, ONNX compute device, and resource limits on the OCR and Recognition page.</em></p>

Python and CUDA are not required. You can instead use Windows OCR with installed system language packs, or select Tesseract and download its language data. All three engines process content locally.

### Recognize, review, and translate

Open an image containing text, select **Text Recognition**, and start recognition. Review the result on the **Text/Barcode** page, where you can correct, copy, or export the text. Click a recognition box in the source image or a recognized line to locate its counterpart.

<p align="center">
  <img src="../Screenshot/5.png" alt="Local OCR results in NimCap Screenshot" width="900">
</p>
<p align="center"><em>Recognition boxes, extracted text, confidence values, and source positions are linked.</em></p>

After checking the text, click **Translate…**. You can translate the entire result or select a passage first. If the result pane is empty, verify that the source image actually contains text. An empty OCR result for a portrait without text does not mean that the image failed to load.

For first-time translation, choose an engine under **Settings → Translation, APIs, and Privacy**:

- Local translation uses a Qwen model and the bundled llama.cpp runtime. Choose CPU or Vulkan GPU and download the selected model plus runtime. The default Qwen3.5 4B Q4_K_M model is about 2.81 GiB, excluding the runtime; 2B, 9B, and 27B options are also available, with 27B Q5_K_M shown only for at least 24 GiB of dedicated VRAM. Save the settings after download; the text remains on your computer.
- Online translation supports MyMemory, the Google web adapter, or LibreTranslate. You must explicitly authorize online processing and confirm the destination and text before sending. Quotas, pricing, and availability are determined by the provider.

<p align="center">
  <img src="../Screenshot/9.png" alt="NimCap Screenshot local translation, online API, and privacy settings" width="900">
</p>
<p align="center"><em>Local translation and online APIs are configured separately; online sending is disabled by default.</em></p>

<p align="center">
  <img src="../Screenshot/6.png" alt="Local translation results in NimCap Screenshot" width="900">
</p>
<p align="center"><em>OCR output can be translated by a local model without uploading it to an external service.</em></p>

Local Qwen translation offers several parameter sizes. Larger models in the same family generally handle complex meaning better, at the cost of more RAM, VRAM, and processing time. Choose a model for your hardware and text; OCR source quality also affects the translation. Review important source text and then verify the translation. Each request accepts up to 6,000 characters and first loading may be slower. A failure does not automatically switch services or compute devices.

### Document field extraction

Select document mode to classify a Chinese ID card, driving license, vehicle license, household register, passport, bank card, or business license using the configured local OCR engine. Change the document type to extract again. No separate document model is needed.

Chinese ID cards support front, back, and combined images. Edit, copy, or export field text as TXT, and click a field line to locate its source. Missing fields stay blank; unclear types retain OCR text. Rotate or flip the source and rerun when necessary.

Field extraction depends on OCR model size, language coverage, and image clarity. You can select a compatible model and rerun recognition, then check names, numbers, dates, and addresses against the source. Extraction covers the current page. It does not verify authenticity, aggregate household-register pages, or provide complete passport MRZ parsing.

### Barcodes, tables, formulas, and hover OCR

- Barcodes: select **QR Code/Barcode** mode and start. No model download is required.
- Tables: use the one-click table-recognition setup under **Settings → OCR and Recognition → Local Tables and Formulas**, save the settings, select a single table, and choose **Table Recognition (Local)**. Review rows, columns, and merged cells before correcting and exporting CSV or XLSX.
- Formulas: configure formula recognition in the same section, save the settings, select one formula, and choose **Formula (Local LaTeX)**. You can edit, copy, or export the result as `.tex`. Recognition quality depends on formula model size, training coverage, and image clarity. Consider a more capable compatible model and check complex symbols, subscripts, and superscripts against the source before export.
- Hover OCR: enable **Hover OCR (This Session)** from the tray or its configured shortcut. Close the menu and hold the pointer over text for about a second; move it to hide the result. Uses configured or cached local OCR, with installed Windows OCR as a fallback when model files are missing. Missing engines and timeouts show an explanation. The enabled state lasts for the current session only.

<a id="cutout"></a>

## 7. One-Click Background Removal

### Download a model and set the default

1. Open **Settings → One-Click Background Removal**.
2. Choose a model under **Select a Model to Download/Configure**.
3. Click **Download and Configure Background Removal** and wait for download, verification, and parameter setup to finish.
4. Select the model you normally want to use under **Default Model**, then click **Save Settings**.

The path, input size, and preprocessing parameters are stored separately for each model. Downloading another model does not replace the default automatically, and you do not need to download every model.

| Original | Background removed |
|---|---|
| <img src="../Screenshot/3.png" alt="Original portrait before background removal" width="360"> | <img src="../Screenshot/4.png" alt="One-click background-removal result in NimCap Screenshot" width="620"> |

| Model | Recommended use | Approximate download |
|---|---|---|
| U²-Net lightweight `u2netp` | Quick tests and lightweight CPU processing | 4.4 MB |
| BiRefNet General Lite | General daily use and the default for a new configuration | 224 MB |
| BiRefNet Portrait | Refined portrait processing | 973 MB |
| BiRefNet Matting | Hair, soft edges, and semitransparent details; community ONNX export | 941 MB |
| BiRefNet General full | Higher-quality general alternative with a heavier workload than Lite | 973 MB |
| BiRefNet HR Matting | Large-image refinement with 2048-level input; community ONNX export | 932 MB |

Start with General Lite, or U²-Net on resource-constrained systems. Add a portrait or matting model only when needed. An HR model is optional and is not guaranteed to outperform a standard model on every image. Model file size is not the same as runtime GPU memory use.

### Remove the background from a copied image

1. Copy an image and choose **Pins** from the tray.
2. Right-click the pin and choose **Remove Background**.
3. Select a configured model and click **Remove Background**.
4. Compare the original on the left with the result on the right. The checkerboard represents transparency.
5. To try another model, select it and rerun. This does not change the default in Settings.
6. Copy, save, or pin the result. Use PNG or a transparency-capable WebP mode to preserve transparency, not JPEG.

Background removal preserves the source resolution and multiplies the generated mask by the image's existing alpha. Hair, glass, and sheer fabric require manual inspection. A transparent mask may not completely remove background colors visible through glass. Continuous live background-removal previews are not currently generated while dragging a selection.

### GPU, timeout, and failure recovery

Choose CPU or DirectML independently under **Settings → Background Removal**. CPU presets are U²-Net and General Lite; Portrait, Matting, full General, and HR are offered with DirectML. The adapter index follows the OCR GPU index. Older configurations without a separate device inherit the ONNX device.

The shared timeout remains under **Settings → OCR and Recognition → Language and Resource Limits**, from 1–120 seconds, default 30. Waiting, loading, preprocessing, and inference can consume the limit. Increase it for slow models, but reduce model workload for memory-allocation errors. Fixed 2048-input models do not shrink when you enter 1024.

Each ONNX operation runs in a separate worker and releases sessions on completion. DirectML initialization/inference errors, insufficient GPU budget, or GPU-worker exits can trigger a CPU retry within the remaining timeout. A failed adapter uses CPU for the rest of this application session; saved preferences remain unchanged. Cancellation and timeout terminate the worker.

For stopped or timed-out operations, try General Lite or U²-Net. For `8007000E` or `E_OUTOFMEMORY`, reduce workload; CPU retries also need system memory. Worker isolation reduces the risk of native GPU failures terminating the application, but real large models and drivers still require device testing.

DirectML does not need CUDA. Local translation uses a separate Vulkan path and does not automatically fall back to CPU.

<a id="recording"></a>

## 8. Recording MP4 or GIF

### Before first use

Open **Settings → Screen Recording**, check the recording environment, and install any missing components shown there. Installing the VC++ x64 runtime may require system confirmation. Windows N/KN editions and other systems without media components require the corresponding Windows components. Microphone and camera access also require Windows privacy permissions.

### Record a screen area

1. Press `Ctrl+Shift+A`, select a rectangular area within one display, and click the video icon. You can also use **Screen Recording → Record Current Display/Show Toolbar…** from the tray.
2. Click the gear on the floating recording toolbar and configure MP4 or GIF, frame rate, quality, capture engine, pointer, click highlighting, and other options.
3. Explicitly enable audio or a camera when needed and confirm the device name. These options are disabled by default, and GIF does not support audio.
4. Click Start and choose a new output file. The default countdown is 3 seconds and can be set from 0 to 10 seconds.
5. Pause or resume during recording; the area border turns orange while paused. After clicking Stop, wait for encoding and finalization to finish.
6. When complete, inspect the output path or open its directory. Do not force the application to exit during finalization.

Closing the configuration window before recording keeps the floating toolbar visible. Closing it during recording does not stop recording. The tray also provides pause, resume, and stop-and-save commands. If the toolbar is collapsed, restore it from the tray.

### Formats and common parameters

| Item | Details |
|---|---|
| MP4 | H.264 video with optional AAC audio; target rates of 15, 30, or 60 fps, subject to capture and encoding performance |
| GIF | No audio, up to 15 fps, longest side from 160 to 800 pixels; maximum 60 seconds and 100 MB |
| Default capture engine | WGC; try DXGI manually for compatibility problems. NimCap Screenshot does not switch silently |
| MP4 bit rate | 0 selects a rate based on quality; 500–100000 kbps requests a fixed rate, in which case the quality preset no longer controls the encoding target. Simple content may still produce a lower actual file bit rate |
| Timed stop | 0 means manual stop; maximum 86,400 seconds. Paused time is excluded |

The recording window displays **actual/target fps**. If it reports an insufficient frame rate, reduce the target rate, select a smaller area, or try another capture engine or encoding method. NimCap Screenshot uses timestamps to preserve the actual recording duration instead of forcing a small number of frames to play at the full rate. After a timed stop, it reports the achieved effective duration and continues saving the file.

Recording supports only a rectangular area on one display. Screenshot annotations and nonrectangular masks are not recorded. The area must be at least 16×16 and at most 7680×4320, with approximately 16.6 million pixels or fewer. MP4 requires even dimensions, so one pixel may be removed from the right or bottom of an odd-sized selection.

NimCap Screenshot attempts to exclude its own recording border and controls, but other windows, pins, and notifications inside the area may still be recorded. Moving the toolbar does not move the recording area. Do not change the resolution or display scaling, disconnect a display, or run a scrolling capture while recording.

Record a short sample first to verify the picture, microphone, and system audio. Mixed audio can produce echo or excessive volume; use headphones and check the selected devices.

### Files after a recording failure

NimCap Screenshot creates a separate temporary directory on the same drive as the output directory. On success, it assembles the final file. On failure, it retains temporary content and provides a **Failed Temporary Files** entry for diagnostics or recovery attempts.

Terminating the process or losing power can leave an MP4 without its final index, and recovery is not guaranteed. Temporary video and GIF frames may also contain private information. Remove them only after confirming they are no longer needed, and do not delete a directory whose origin you have not verified.

<a id="settings"></a>

<a id="data-directory"></a>

## 9. Settings, Local Data, and Privacy

The Settings window is organized by category. Scroll down on the right when a page contains more options. After making changes, click **Save Settings** at the bottom. A successful model download does not mean the configuration has been saved.

Application settings and local data default to:

```text
The folder containing NimCap.Screenshot.exe
```

In **Settings → General → Local Data**, browse or enter a new folder, or click **Use executable folder**. Saving prompts you to migrate downloaded models, settings, and history. After confirmation, exit NimCap Screenshot from the tray and launch it again to perform the migration and update model and history paths. Original data is deleted only after success; existing application data in the destination is never overwritten. If migration fails, the original folder remains in use.

The legacy data folder is `%LocalAppData%\SnapSharp`; the rename preserves that path. At startup, the app first reads `data-location.json` beside the executable, uses its recorded current directory, and applies any confirmed pending migration. Without that file, it uses the legacy folder only when the executable folder has no managed data entries and the legacy folder contains entries such as `settings.json`, `models`, or `History`. Otherwise it uses the executable folder. If both folders contain data, the app does not merge them automatically.

Use the controls above to migrate legacy data to the executable folder or another folder. Preserve the data folder and `data-location.json` during a manual upgrade. If you extract the app into a different folder, check the data location: the rename alone does not locate data beside the old executable or in a custom folder. Both the executable folder and the selected data folder must be writable.

| Directory or file | Contents |
|---|---|
| `settings.json` | Personal settings, shortcuts, and model paths |
| `models` | Downloaded models and language data |
| `History` | Screenshot history, thumbnails, and indexes |
| `Diagnostics` | Diagnostic information and self-test results under the current data folder |
| `Plugins` | Discovery directory for external plugins, which are disabled by default |

When updating, preserve the data folder and `data-location.json`; do not delete the entire application folder. After manually moving to another computer, you may need to select local model paths again. Before deleting model files, confirm that no feature still references them.

Local screenshots, OCR, background removal, and local translation do not upload images or text. Model downloads require internet access. Online translation, reverse image search, and similar features send relevant content to external services and require explicit authorization and review of the outgoing preview.

Screenshot history is not encrypted. History images, scrolling-capture projects, and temporary recording files may contain sensitive information. Do not treat local storage as an encrypted vault. External plugins run inside the NimCap Screenshot process rather than a security sandbox; verify their source before enabling them.

<a id="faq"></a>

## 10. Quick Troubleshooting

| Symptom | Check first |
|---|---|
| No window appears after launch | Find NimCap Screenshot in the notification area or hidden icons; this is the normal startup behavior |
| A shortcut does not respond | Check whether another application uses it, then assign and save another shortcut |
| Clipboard image recognition cannot read an image | Confirm that you copied image content, not a URL, text path, or only a file object |
| The image appears, but OCR is empty | The image may contain no text; switch to background-removal mode when that is the intended task |
| A model is reported missing | Download and configure it on the relevant Settings page, then save. Do not merely change a file extension |
| The background-removal page has no timeout option | Adjust it under **Settings → OCR and Recognition → Language and Resource Limits** |
| A large model reports insufficient memory or crashes | Stop using the HR + DirectML combination, fully exit, then test a lightweight model or CPU |
| A cutout has a white background after saving | Use PNG instead of JPEG, BMP, or another format that does not preserve transparency |
| A pin cannot be clicked | Assign a shortcut or enable the tray entry for disabling pin click-through in shortcut settings |
| A scrolling capture does not finish automatically | Manual mode requires you to click `√`; reaching the bottom does not finish it |
| A scrolling capture is misaligned | Scroll more slowly, avoid dynamic content, exclude fixed bars, or fine-tune the seams |
| A recording has no sound | Select MP4, explicitly enable an audio device, and grant system permission |
| Recording failed or the file cannot be opened | Wait for finalization and inspect the failed temporary files; do not terminate the process |

For everyday use, start with **keyboard shortcut → annotate directly → copy or save**. To process an existing image, pin it to the desktop and open **Image Tools** from the pin's context menu.

## CLI automation

For scripted screenshots, use the independent `nimcap.exe` included in the portable package:

```powershell
.\nimcap.exe capture --full --output .\screen.png --json
```

It supports physical rectangles, foreground or specified windows, monitor capture, overwrite control, timeout, cancellation and machine-readable results. Discover targets with `windows --json` and `monitors --json`; select a unique window with `--process name` and/or `--window-title text`. Multiple matches fail explicitly. `doctor --backend auto --json` tests desktop access, current-directory writes and real capture. An interactive desktop is required. See [CLI automation and English quick reference](CLI.md).

## Startup Update Checks

**SnapSharp v1.1.3 and earlier require a manual upgrade using the complete new package.** Download the Windows x64 archive for v1.1.4 or later from [GitHub Releases](https://github.com/nimcap/nimcap-screenshot/releases), extract it fully, and run `NimCap.Screenshot.exe`. Future updates can use the new in-app updater. Preserve the data folder and `data-location.json` beside the executable, and check the [data-folder selection rules](#data-directory). See the [v1.1.4 release notes](releases/v1.1.4.md) for details of the rename.

NimCap Screenshot checks GitHub Releases once on each launch and reports whether a new stable version is available. It does not poll during use. **Settings → About → Online Update** reuses that result; click **Check for updates** to request another check explicitly. When a new version is found, choose **Download and Install Update**; you can also open the release page. Release notes are not embedded below the status. Downloads can be canceled. The app verifies SHA256, the complete package structure, and the target version before allowing installation.

After the download, confirm whether to restart and install immediately. Save unfinished edits and settings, and finish recording, scrolling capture, and model downloads first. The independent updater waits for the app to exit, backs up files being replaced, installs into the current application folder, and launches the new version. Saved settings, models, history, and files outside the release package are preserved.

If file replacement fails, the helper restores replaced files and attempts to restart the previous app. If permissions or file locks also prevent rollback, it shows the backup path for recovery. Backups and installation results are stored under `NimCap.Screenshot-updates/<update-id>/` in the system temporary folder. This rollback covers installation failures, not a health check of the new version.

The current user needs write access to the application folder; the updater does not request administrator privileges automatically. Retry later if GitHub is unavailable or rate-limited, checksum information is missing, or disk space is insufficient. A download error does not close the running app before installation.

## 11. License

Copyright 2026 leelongjiang and NimCap Screenshot contributors

NimCap Screenshot is licensed under the [Apache License 2.0](../LICENSE). Third-party components, models, and services retain their respective licenses and terms; see [THIRD-PARTY-NOTICES.md](../THIRD-PARTY-NOTICES.md) for details.
