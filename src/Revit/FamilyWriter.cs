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
                var graphics = new AnnotationGraphics(input.FeetPerUnit);
                var result = PlaceParts(target, view, source, input, graphics, existing == null, work, temporaryFiles);
                if (existing == null)
                    target.SaveAs(output, new SaveAsOptions { OverwriteExistingFile = true, Compact = true });
                var levels = input.Parts.Where(p => p.Role == Role.Geometry).SelectMany(p => p.Buildings)
                    .Where(Composition.IsParking).Select(Visibility.BuildingParameterName)
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                return $"Перенесено CAD-объектов: {graphics.WrittenItems}. Вложенных семейств: {result.NestedCount}." +
                    (levels.Length == 0 ? "" : "\nПостроены уровни: " + string.Join(", ", levels) + ".") +
                    (graphics.SkippedItems == 0 ? "" : $" Пропущено при построении: {graphics.SkippedItems}.") +
                    (result.Unmoved.Count == 0 ? "" : "\nRevit сохранил прежнюю группу параметров: " + string.Join(", ", result.Unmoved)) +
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

        private bool BuildNestedFamily(string template, string path, IEnumerable<SchemePart> parts, AnnotationGraphics graphics)
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
                    bool written = false;
                    foreach (var part in parts) written |= graphics.Write(child, view, part);
                    if (!written)
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

        private (int NestedCount, IReadOnlyList<string> Unmoved) PlaceParts(Document target, View view, string source,
            SchemeDialog input, AnnotationGraphics graphics, bool newFamily, string work, List<string> temporaryFiles)
        {
            var schema = GetSchema();
            var fallback = new List<(SchemePart Owner, SchemePart Drawing)>();
            var directNotes = new List<SchemePart>();
            using (var group = new TransactionGroup(target, "RevitCreateZhuk: построить схему"))
            {
                if (group.Start() != TransactionStatus.Started)
                    throw new InvalidOperationException("Revit не смог начать построение схемы.");

                Visibility visibility;
                using (var transaction = new Transaction(target, "Жук: элементы Revit"))
                {
                    FamilyDocuments.Start(transaction);
                    if (newFamily) ClearTemplateNotes(target);
                    bool replaced = DeletePrevious(target, schema, source, view);
                    visibility = new Visibility(target, input.Parts, replaced);
                    if (replaced) visibility.RemoveUnusedLegacyParameters();
                    visibility.ApplyInstanceParameters();

                    foreach (var part in input.Parts)
                    {
                        bool IsDirectNote(CadItem item) => input.NativeMarkText && Composition.IsMarkText(item);
                        var notes = part.Items.Where(IsDirectNote).ToArray();
                        if (notes.Length > 0) directNotes.Add(part.WithItems(notes));

                        var remaining = new List<CadItem>();
                        foreach (var batch in part.Items.Where(i => !IsDirectNote(i)).GroupBy(i => i.Text != null))
                            if (!graphics.TryWriteNative(target, view, part.WithItems(batch), element =>
                            {
                                if (!visibility.TryBind(element, part)) return false;
                                Tag(element);
                                return true;
                            }))
                                remaining.AddRange(batch);
                        if (remaining.Count > 0) fallback.Add((part, part.WithItems(remaining)));
                    }

                    graphics.WriteLabels(target, view, directNotes, Tag);
                    visibility.OrderToggles();
                    FamilyDocuments.Commit(transaction);
                }

                var files = new List<(SchemePart Owner, string Path)>();
                var names = new FilteredElementCollector(target).OfClass(typeof(Family)).Cast<Family>()
                    .Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var items in fallback.GroupBy(p => visibility.ControlName(p.Owner), StringComparer.OrdinalIgnoreCase))
                {
                    string stem = System.Text.RegularExpressions.Regex.Replace(items.Key, @"[^p{L}p{Nd} ._-]", "_");
                    stem = "Жук - " + stem.Substring(0, Math.Min(stem.Length, 70)).TrimEnd(' ', '.');
                    string name = stem;
                    for (int suffix = 2; !names.Add(name); suffix++) name = stem + " " + suffix;
                    string file = Path.Combine(work, name + ".rfa");
                    temporaryFiles.Add(file);
                    if (BuildNestedFamily(input.TemplatePath, file, items.Select(p => p.Drawing), graphics))
                        files.Add((items.First().Owner, file));
                }

                if (files.Count != 0)
                {
                    using (var transaction = new Transaction(target, "Жук: группы без нативной связи видимости"))
                    {
                        FamilyDocuments.Start(transaction);
                        foreach (var entry in files)
                        {
                            if (!target.LoadFamily(entry.Path, out var family))
                                throw new InvalidOperationException("Не удалось загрузить группу «" + visibility.ControlName(entry.Owner) + "».");
                            var symbol = (FamilySymbol)target.GetElement(family.GetFamilySymbolIds().Single());
                            if (!symbol.IsActive) symbol.Activate();
                            var instance = target.FamilyCreate.NewFamilyInstance(view.Origin, symbol, view);
                            if (!visibility.TryBind(instance, entry.Owner))
                                throw new InvalidOperationException("Не удалось связать видимость группы «" + visibility.ControlName(entry.Owner) + "».");
                            Tag(instance);
                        }
                        FamilyDocuments.Commit(transaction);
                    }
                }
                if (graphics.WrittenItems == 0)
                    throw new InvalidOperationException("Масштаб не оставил объектов допустимого размера.");
                if (group.Assimilate() != TransactionStatus.Committed)
                    throw new InvalidOperationException("Revit отменил построение схемы.");
                return (files.Count, visibility.UnmovedParameters.Distinct().ToArray());
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
            var previous = new FilteredElementCollector(target).WherePasses(new ExtensibleStorageFilter(SchemaId))
                .WhereElementIsNotElementType().Where(BelongsToSource).ToList();
            if (previous.Count == 0) return false;

            var ids = previous.Select(i => i.Id).ToHashSet();
            var usedElsewhere = all.Where(i => !ids.Contains(i.Id)).Select(i => i.Symbol.Family.Id).ToHashSet();
            var unused = previous.OfType<FamilyInstance>().Select(i => i.Symbol.Family.Id)
                .Distinct().Where(id => !usedElsewhere.Contains(id)).ToList();
            target.Delete(ids.ToList());
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
