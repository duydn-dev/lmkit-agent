using System;
using System.ComponentModel;
using LMKit.Agents.Tools;
using ImageMagick;

namespace LmKitOmniApi.Infrastructure.Tools.Images
{
    public partial class ImageTools
    {
        [LMFunction("Image_GetEXIF", "Auto-generated stub for GetEXIF")]
        public string GetEXIF()
        {
            throw new NotImplementedException("GetEXIF is not fully implemented yet.");
        }

        [LMFunction("Image_UpdateEXIF", "Auto-generated stub for UpdateEXIF")]
        public string UpdateEXIF()
        {
            throw new NotImplementedException("UpdateEXIF is not fully implemented yet.");
        }

        [LMFunction("Image_RemoveEXIF", "Auto-generated stub for RemoveEXIF")]
        public string RemoveEXIF()
        {
            throw new NotImplementedException("RemoveEXIF is not fully implemented yet.");
        }

        [LMFunction("Image_GetIPTC", "Auto-generated stub for GetIPTC")]
        public string GetIPTC()
        {
            throw new NotImplementedException("GetIPTC is not fully implemented yet.");
        }

        [LMFunction("Image_UpdateIPTC", "Auto-generated stub for UpdateIPTC")]
        public string UpdateIPTC()
        {
            throw new NotImplementedException("UpdateIPTC is not fully implemented yet.");
        }

        [LMFunction("Image_RemoveMetadata", "Auto-generated stub for RemoveMetadata")]
        public string RemoveMetadata()
        {
            throw new NotImplementedException("RemoveMetadata is not fully implemented yet.");
        }

        [LMFunction("Image_SetCopyright", "Auto-generated stub for SetCopyright")]
        public string SetCopyright()
        {
            throw new NotImplementedException("SetCopyright is not fully implemented yet.");
        }

        [LMFunction("Image_SetAuthor", "Auto-generated stub for SetAuthor")]
        public string SetAuthor()
        {
            throw new NotImplementedException("SetAuthor is not fully implemented yet.");
        }

        [LMFunction("Image_SetGPSLocation", "Auto-generated stub for SetGPSLocation")]
        public string SetGPSLocation()
        {
            throw new NotImplementedException("SetGPSLocation is not fully implemented yet.");
        }

        [LMFunction("Image_StripMetadata", "Removes all metadata (EXIF, IPTC, etc.) from the image.")]
        public string StripMetadata()
        {
            if (!CheckIfOpen()) return "No image is open.";
            _image!.Strip();
            return "All metadata stripped from the image.";
        }
    }
}
