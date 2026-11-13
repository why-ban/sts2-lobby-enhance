# Workshop upload workspace

This folder is a workspace for the official
[`MegaCrit/sts2-mod-uploader`](https://github.com/MegaCrit/sts2-mod-uploader) (v0.2.0).
`ModUploader.exe upload -w <this folder>` reads it as-is.

```
packaging/
  workshop.json        item title / description / visibility / dependencies
  mod_id.txt           the workshop item id (3839101755) - without it the next upload
                       creates a SECOND item, and subscribers cannot be migrated
  image.png            512x512 preview, must stay under 1 MiB (Steam's limit)
  content/             the only thing that is actually uploaded
    sts2_lobby_enhance.dll
    sts2_lobby_enhance.json
    LICENSE
    THIRD_PARTY_NOTICES
  make-image.py        regenerates image.png (Pillow)
  build-content.ps1    rebuilds src/ and refreshes content/
  README.md            this file (not uploaded)
```

`content/` is generated: run `build-content.ps1` instead of copying files by hand. The regression suite
(`tests/`) checks that `content/sts2_lobby_enhance.dll` is byte-identical to the current build
output, that `content/` contains nothing but the four files above, and that `image.png` is a square PNG
below 1 MiB.

Full upload and update procedure: [`../docs/UPLOAD.md`](../docs/UPLOAD.md).
