# Unity Package Manager Thumbnails

Shows each asset's image next to its name in the **My Assets** list of Unity's built-in Package Manager.

![Before and after](Documentation~/before%20and%20after.png)

## Features

- Square, center-cropped thumbnail next to each asset name.
- Slider to change the thumbnail size.
- Switch between displaying long asset names as multiline or truncated.
- Your settings are remembered between sessions.

The controls are at the bottom of the left sidebar in the Package Manager window, below the My Assets / Unity Registry list.

## Requirements

- Unity **6000.3** or newer.

This package was developed and tested on Unity 6000.3.7f1. It may work on older versions, but that is untested.

It is editor-only and works by reaching into the Package Manager's internal UI, which is not public API. If a future Unity version changes that UI, the thumbnails may stop appearing.

## How to install

1. In Unity, open **Window > Package Manager**.
2. Click the **+** button in the top-left corner and choose **Install package from git URL...**
3. Enter the following URL and click **Install**:

   ```
   https://github.com/EricFreeman/PackageManagerThumbnails.git
   ```
