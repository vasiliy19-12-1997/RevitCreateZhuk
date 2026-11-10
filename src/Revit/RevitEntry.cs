using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using View = Autodesk.Revit.DB.View;

namespace RevitCreateZhuk
{
    public sealed class RevitApplication : IExternalApplication
    {
        public Result OnStartup(UIControlledApplication application)
        {
            var panel = application.CreateRibbonPanel("RevitCreateZhuk");
            panel.AddItem(new PushButtonData("RevitCreateZhuk", "Создать\nсхему-жук",
                typeof(CreateCommand).Assembly.Location, typeof(CreateCommand).FullName)
            {
                AvailabilityClassName = typeof(CommandAvailability).FullName,
                ToolTip = "Построить семейство типовой аннотации из DWG."
            });
            return Result.Succeeded;
        }

        public Result OnShutdown(UIControlledApplication application) => Result.Succeeded;
    }

    public sealed class CommandAvailability : IExternalCommandAvailability
    {
        public bool IsCommandAvailable(UIApplication application, CategorySet categories)
        {
            var document = application.ActiveUIDocument?.Document;
            return document == null || (!document.IsModifiable && !document.IsReadOnly);
        }
    }

    [Transaction(TransactionMode.Manual)]
    public sealed class CreateCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            var ui = data.Application;
            var current = ui.ActiveUIDocument?.Document;
            if (FamilyDocuments.IsAnnotation(current) == false) current = null;
            var owner = new RevitWindow(ui.MainWindowHandle);
            string template = FamilyDocuments.FindTemplate(ui.Application.FamilyTemplatePath);
            string target = current == null ? "Результат · новое семейство типовой аннотации" : "Результат · текущее семейство «" + current.Title + "»";
            try
            {
                using (var dialog = new SchemeDialog(template, target))
                {
                    if (dialog.ShowDialog(owner) != DialogResult.OK) return Result.Cancelled;
                    string output = null;
                    if (current == null)
                    {
                        using (var save = new SaveFileDialog { Filter = "Семейство Revit (*.rfa)|*.rfa",
                            DefaultExt = "rfa", AddExtension = true, OverwritePrompt = true,
                            FileName = "ТипАн_" + Path.GetFileNameWithoutExtension(dialog.SourcePath) + "_Схема" })
                        {
                            if (save.ShowDialog(owner) != DialogResult.OK) return Result.Cancelled;
                            output = save.FileName;
                        }
                        if (ui.Application.Documents.Cast<Document>().Any(d =>
                            string.Equals(d.PathName, output, StringComparison.OrdinalIgnoreCase)))
                            throw new InvalidOperationException("Выберите путь, который сейчас не открыт в Revit.");
                    }

                    using (var source = OpenUnchanged(dialog.SourcePath, dialog.SourceLength, dialog.SourceWriteTime))
                    using (var parking = OpenUnchanged(dialog.ParkingPath, dialog.ParkingLength, dialog.ParkingWriteTime))
                    {
                        var writer = new FamilyWriter(ui.Application);
                        string report = writer.Write(current, output, dialog);
                        if (output != null)
                        {
                            try { ui.OpenAndActivateDocument(output); }
                            catch (Exception exception)
                            { report += "\nСемейство сохранено. Откройте его через «Файл → Открыть».\n" + exception.Message; }
                        }
                        TaskDialog.Show("RevitCreateZhuk", report);
                    }
                }
                return Result.Succeeded;
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException) { return Result.Cancelled; }
            catch (Exception exception)
            {
                TaskDialog.Show("RevitCreateZhuk", exception.Message);
                return Result.Cancelled;
            }
        }

        private static FileStream OpenUnchanged(string path, long length, DateTime timestamp)
        {
            if (string.IsNullOrEmpty(path)) return null;
            var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (file.Length == length && File.GetLastWriteTimeUtc(path) == timestamp) return file;
            file.Dispose();
            throw new InvalidOperationException("DWG изменился после анализа. Выберите файл заново: " + Path.GetFileName(path));
        }

        private sealed class RevitWindow : IWin32Window
        {
            public RevitWindow(IntPtr handle) { Handle = handle; }
            public IntPtr Handle { get; }
        }
    }

    internal static class FamilyDocuments
    {
        public static bool IsAnnotation(Document document) =>
            document != null && document.IsFamilyDocument &&
            document.OwnerFamily?.FamilyCategory?.Id.Value == (long)BuiltInCategory.OST_GenericAnnotation;

        public static View AnnotationView(Document document)
        {
            var active = document.ActiveView;
            if (Suitable(active)) return active;
            return new FilteredElementCollector(document).OfClass(typeof(View)).Cast<View>()
                .FirstOrDefault(Suitable) ?? throw new InvalidOperationException("В семействе нет вида типовой аннотации.");
        }

        private static bool Suitable(View view) => view != null && !view.IsTemplate && view.CanBePrinted &&
            (view.ViewType == ViewType.DrawingSheet || view.ViewType == ViewType.DraftingView || view.ViewType == ViewType.FloorPlan);

        public static string FindTemplate(string configured)
        {
            string[] names = { "Метрическая система, типовая аннотация.rft", "Типовая аннотация.rft",
                "Metric Generic Annotation.rft", "Generic Annotation.rft" };
            string[] roots = { configured,
                Path.Combine(Path.GetDirectoryName(typeof(Document).Assembly.Location) ?? "", "..", "Family Templates"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Autodesk", "RVT 2024", "Family Templates") };
            foreach (string root in roots.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
                foreach (string name in names)
                {
                    try
                    {
                        string path = Directory.EnumerateFiles(root, name, SearchOption.AllDirectories).FirstOrDefault();
                        if (path != null) return Path.GetFullPath(path);
                    }
                    catch (UnauthorizedAccessException) { }
                    catch (IOException) { }
                }
            return null;
        }

        public static void Start(Transaction transaction)
        {
            if (transaction.Start() != TransactionStatus.Started)
                throw new InvalidOperationException("Revit не смог начать операцию.");
            transaction.SetFailureHandlingOptions(transaction.GetFailureHandlingOptions().SetClearAfterRollback(true)
                .SetForcedModalHandling(true));
        }

        public static void Commit(Transaction transaction)
        {
            if (transaction.Commit() != TransactionStatus.Committed)
                throw new InvalidOperationException("Revit отменил транзакцию «" + transaction.GetName() + "».");
        }
    }
}

