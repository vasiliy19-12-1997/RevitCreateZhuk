using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using RevitCreateZhuk.Core;

namespace RevitCreateZhuk
{
    internal sealed class FamilyWriter
    {
        private static readonly Guid SchemaId = new Guid("7a5494ee-55df-4050-9b3a-896a38263320");
        private readonly Application application;

        public FamilyWriter(Application application) { this.application = application; }

        public string Write(Document existing, string output, SchemeDialog input)
        {
            string source = Path.GetFullPath(input.SourcePath).ToUpperInvariant();
            string work = Path.Combine(Path.GetTempPath(), "RevitCreateZhuk_" + Guid.NewGuid().ToString("N"));
            var temporaryFiles = new List<string>();
            Document target = null;
            Directory.CreateDirectory(work);
            try
            {
                target = existing ?? application.NewFamilyDocument(input.TemplatePath);
                RequireAnnotation(target);
                View view = FamilyDocuments.AnnotationView(target);
                ImportSource(target, view, input.SourcePath);
                if (input.ParkingPath != null) ImportSource(target, view, input.ParkingPath);
                var files = new Dictionary<SchemePart, string>();
                var directNotes = new List<SchemePart>();
                var graphics = new AnnotationGraphics(input.FeetPerUnit);
                foreach (var part in input.Parts)
                {
                    var nestedPart = part;
                    if (input.NativeMarkText)
                    {
                        var notes = part.Items.Where(Composition.IsMarkText).ToArray();
                        if (notes.Length > 0)
                        {
                            directNotes.Add(part.WithItems(notes));
                            nestedPart = part.WithItems(part.Items.Where(i => !Composition.IsMarkText(i)));
                        }
                    }
                    if (nestedPart.Items.Count == 0) continue;
                    string file = Path.Combine(work, "Жук_" + Visibility.Hash(Guid.NewGuid().ToString()) + ".rfa");
                    temporaryFiles.Add(file);
                    if (BuildNestedFamily(input.TemplatePath, file, nestedPart, graphics))
                        files.Add(nestedPart, file);
                }
                if (files.Count == 0 && directNotes.Count == 0)
                    throw new InvalidOperationException("Масштаб не оставил объектов допустимого размера.");
                var unmoved = PlaceParts(target, view, source, files, directNotes, graphics, existing == null);
                if (existing == null)
                    target.SaveAs(output, new SaveAsOptions { OverwriteExistingFile = true, Compact = true });
                var levels = files.Keys.Where(p => p.Role == Role.Geometry).SelectMany(p => p.Buildings)
                    .Where(Composition.IsParking).Select(Visibility.BuildingParameterName)
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                return $"Построено групп: {files.Count}. Перенесено CAD-объектов: {graphics.WrittenItems}." +
                    (directNotes.Count == 0 ? "" : $"\nРедактируемых подписей разрезов и фасадов: {directNotes.Sum(p => p.Items.Count)}.") +
                    (levels.Length == 0 ? "" : "\nПостроены уровни: " + string.Join(", ", levels) + ".") +
                    (graphics.SkippedItems == 0 ? "" : $" Пропущено при построении: {graphics.SkippedItems}.") +
                    (unmoved.Count == 0 ? "" : "\nRevit сохранил прежнюю группу параметров: " + string.Join(", ", unmoved)) +
                    (existing == null ? "\nСемейство сохранено в " + output : "\nИзменения внесены в текущее семейство. Сохраните его в Revit.");
            }
            finally
            {
                try { if (existing == null && target != null && target.IsValidObject) target.Close(false); }
                finally
                {
                    foreach (string file in temporaryFiles) TryClean(() => File.Delete(file));
                    TryClean(() => Directory.Delete(work));
                }
            }
        }

        private static void RequireAnnotation(Document document)
        {
            if (!FamilyDocuments.IsAnnotation(document))
                throw new InvalidOperationException("Выбранный RFT должен иметь категорию «Типовые аннотации».");
        }

        private static void ImportSource(Document target, View view, string source)
        {
            using (var transaction = new Transaction(target, "Жук: импорт исходного DWG"))
            using (var options = new DWGImportOptions
            {
                ThisViewOnly = true, VisibleLayersOnly = true, Placement = ImportPlacement.Centered
            })
            {
                FamilyDocuments.Start(transaction);
                try
                {
                    if (!target.Import(source, options, view, out var id) || id == ElementId.InvalidElementId)
                        throw new InvalidOperationException("Revit не смог импортировать выбранный DWG.");
                }
                finally { transaction.RollBack(); }
            }
        }

        private bool BuildNestedFamily(string template, string path, SchemePart part, AnnotationGraphics graphics)
        {
            var child = application.NewFamilyDocument(template);
            try
            {
                RequireAnnotation(child);
                var view = FamilyDocuments.AnnotationView(child);
                using (var transaction = new Transaction(child, "Жук: построение аннотации"))
                {
                    FamilyDocuments.Start(transaction);
                    ClearTemplateNotes(child);
                    if (child.FamilyManager.Types.Size == 0) child.FamilyManager.NewType("Основной");
                    if (!graphics.Write(child, view, part))
                    {
                        transaction.RollBack();
                        return false;
                    }
                    FamilyDocuments.Commit(transaction);
                }
                child.SaveAs(path, new SaveAsOptions { Compact = true });
                return true;
            }
            finally { child.Close(false); }
        }

        private static IReadOnlyList<string> PlaceParts(Document target, View view, string source,
            Dictionary<SchemePart, string> files, IReadOnlyList<SchemePart> directNotes, AnnotationGraphics graphics, bool newFamily)
        {
            var schema = GetSchema();
            using (var transaction = new Transaction(target, "RevitCreateZhuk: построить схему"))
            {
                FamilyDocuments.Start(transaction);
                if (newFamily) ClearTemplateNotes(target);
                bool replaced = DeletePrevious(target, schema, source, view);
                var visibility = new Visibility(target, files.Keys, replaced);
                if (replaced) visibility.RemoveUnusedLegacyParameters();
                visibility.ApplyInstanceParameters();
                foreach (var entry in files)
                {
                    if (!target.LoadFamily(entry.Value, out var family))
                        throw new InvalidOperationException("Не удалось загрузить группу «" + SchemeDialog.RoleName(entry.Key.Role) + "».");
                    var symbol = (FamilySymbol)target.GetElement(family.GetFamilySymbolIds().Single());
                    if (!symbol.IsActive) symbol.Activate();
                    var instance = target.FamilyCreate.NewFamilyInstance(view.Origin, symbol, view);
                    visibility.Bind(instance, entry.Key);
                    Tag(instance);
                }
                graphics.WriteLabels(target, view, directNotes, Tag);
                visibility.OrderToggles();
                FamilyDocuments.Commit(transaction);
                return visibility.UnmovedParameters.Distinct().ToArray();
            }

            void Tag(Element element)
            {
                var marker = new Entity(schema);
                marker.Set("Source", source);
                marker.Set("View", view.UniqueId);
                element.SetEntity(marker);
            }
        }

        private static void ClearTemplateNotes(Document document)
        {
            var notes = new FilteredElementCollector(document).OfClass(typeof(TextNote)).ToElementIds();
            if (notes.Count > 0) document.Delete(notes);
        }

        private static bool DeletePrevious(Document target, Schema schema, string source, View view)
        {
            var all = new FilteredElementCollector(target).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>().ToList();
            bool BelongsToSource(Element element)
            {
                var entity = element.GetEntity(schema);
                return entity.IsValid() && entity.Get<string>("Source") == source &&
                    entity.Get<string>("View") == view.UniqueId;
            }
            var previous = all.Where(BelongsToSource).ToList();
            var notes = new FilteredElementCollector(target).OfClass(typeof(TextNote)).OwnedByView(view.Id)
                .WhereElementIsNotElementType().Where(BelongsToSource).Select(n => n.Id).ToList();
            if (previous.Count == 0 && notes.Count == 0) return false;

            var ids = previous.Select(i => i.Id).ToHashSet();
            var usedElsewhere = all.Where(i => !ids.Contains(i.Id)).Select(i => i.Symbol.Family.Id).ToHashSet();
            var unused = previous.Select(i => i.Symbol.Family.Id).Distinct().Where(id => !usedElsewhere.Contains(id)).ToList();
            target.Delete(ids.Concat(notes).ToList());
            if (unused.Count > 0) target.Delete(unused);
            return true;
        }

        private static Schema GetSchema()
        {
            var schema = Schema.Lookup(SchemaId);
            if (schema != null) return schema;
            var builder = new SchemaBuilder(SchemaId);
            builder.SetSchemaName("RevitCreateZhukGeneratedAnnotation");
            builder.AddSimpleField("Source", typeof(string));
            builder.AddSimpleField("View", typeof(string));
            return builder.Finish();
        }

        private static void TryClean(Action action)
        {
            try { action(); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}

