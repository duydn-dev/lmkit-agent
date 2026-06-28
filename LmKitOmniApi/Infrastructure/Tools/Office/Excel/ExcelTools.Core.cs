using System;
using LMKit.Agents.Tools;
using System.ComponentModel;
using Aspose.Cells;

namespace LmKitOmniApi.Infrastructure.Tools.Office.Excel
{
    public partial class ExcelTools
    {
        [LMFunction("Excel_CreateWorkbook", "Create a new Microsoft Excel workbook and save it to the specified path.")]
        public string CreateWorkbook([Description("The absolute path where the new .xlsx file will be saved")] string filePath)
        {
            _workbook = new Workbook();
            _workbook.Save(filePath);
            _currentFilePath = filePath;
            _isOpen = true;
            return $"Successfully created workbook at {filePath}";
        }

        [LMFunction("Excel_OpenWorkbook", "Open an existing Microsoft Excel workbook.")]
        public string OpenWorkbook([Description("The absolute path of the .xlsx file to open")] string filePath)
        {
            _workbook = new Workbook(filePath);
            _currentFilePath = filePath;
            _isOpen = true;
            return $"Successfully opened workbook from {filePath}";
        }

        [LMFunction("Excel_SaveWorkbook", "Save the currently opened Microsoft Excel workbook.")]
        public string SaveWorkbook()
        {
            if (!_isOpen || _workbook == null || _currentFilePath == null) return "No workbook is currently open.";
            _workbook.Save(_currentFilePath);
            return "Workbook saved successfully.";
        }

        [LMFunction("Excel_ReadCell", "Read a value from a specific cell in the active worksheet.")]
        public string ReadCell([Description("The name of the worksheet (e.g. 'Sheet1')")] string sheetName, [Description("The cell reference (e.g. 'A1', 'B2')")] string cellReference)
        {
            if (!_isOpen || _workbook == null) return "No workbook is currently open.";
            var sheet = _workbook.Worksheets[sheetName];
            if (sheet == null) return $"Worksheet '{sheetName}' not found.";
            return sheet.Cells[cellReference].StringValue;
        }

        [LMFunction("Excel_WriteCell", "Write a text or numeric value into a specific cell in the active worksheet.")]
        public string WriteCell([Description("The name of the worksheet")] string sheetName, [Description("The cell reference (e.g. 'A1', 'B2')")] string cellReference, [Description("The value to write")] string value)
        {
            if (!_isOpen || _workbook == null) return "No workbook is currently open.";
            var sheet = _workbook.Worksheets[sheetName];
            if (sheet == null) return $"Worksheet '{sheetName}' not found.";
            sheet.Cells[cellReference].PutValue(value);
            return "Value written successfully.";
        }

        [LMFunction("Excel_SetFormula", "Set the formula for a specific cell.")]
        public string SetFormula([Description("The name of the worksheet")] string sheetName, [Description("The cell reference (e.g. 'C1')")] string cellReference, [Description("The formula (e.g. '=A1+B1')")] string formula)
        {
            if (!_isOpen || _workbook == null) return "No workbook is currently open.";
            var sheet = _workbook.Worksheets[sheetName];
            if (sheet == null) return $"Worksheet '{sheetName}' not found.";
            sheet.Cells[cellReference].Formula = formula;
            return "Formula set successfully.";
        }
    }
}
