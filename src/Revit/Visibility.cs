using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB;
using RevitCreateZhuk.Core;

namespace RevitCreateZhuk
{
    internal sealed class Visibility
    {
        private readonly FamilyManager manager;
        private readonly Dictionary<string, FamilyParameter> parameters;
        private readonly Dictionary<ElementId, int> toggleOrder = new Dictionary<ElementId, int>();
        private readonly Dictionary<FamilyParameter, string> formulas = new Dictionary<FamilyParameter, string>();
        private readonly Dictionary<SchemePart, FamilyParameter> controls = new Dictionary<SchemePart, FamilyParameter>();
        private readonly FamilyParameter[] parking;
        private readonly bool replacing;
        private readonly List<string> unmoved = new List<string>();

        public IReadOnlyList<string> UnmovedParameters => unmoved;

        public Visibility(Document document, IEnumerable<SchemePart> parts, bool replacing)
        {
            manager = document.FamilyManager;
            this.replacing = replacing;
            if (manager.Types.Size == 0) manager.NewType("Основной");
            if (manager.CurrentType == null) manager.CurrentType = manager.Types.Cast<FamilyType>().First();
            parameters = manager.Parameters.Cast<FamilyParameter>()
                .ToDictionary(p => p.Definition.Name, StringComparer.OrdinalIgnoreCase);
            var names = parts.SelectMany(p => p.Buildings).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            foreach (var name in names.OrderBy(Group).ThenBy(n => n, StringComparer.CurrentCultureIgnoreCase))
            {
                Toggle(BuildingParameterName(name), !Composition.IsParking(name), LegacyBuildingName(name), Group(name));
            }
            parking = names.Where(Composition.IsParking).Select(n => parameters[BuildingParameterName(n)])
                .Distinct().OrderBy(p => p.Definition.Name, StringComparer.OrdinalIgnoreCase).ToArray();
            foreach (var part in parts) controls.Add(part, Resolve(part));
        }

        public void Bind(FamilyInstance instance, SchemePart part)
        {
            var control = controls[part];
            var parameter = instance.get_Parameter(BuiltInParameter.IS_VISIBLE_PARAM);
            if (parameter == null || !manager.CanElementParameterBeAssociated(parameter))
                throw new InvalidOperationException("Revit не разрешил связать видимость группы «" +
                    control.Definition.Name + "».");
            manager.AssociateElementParameterToFamilyParameter(parameter, control);
        }

        private FamilyParameter Resolve(SchemePart part)
        {
            if (!string.IsNullOrWhiteSpace(part.Mark)) return Toggle(part.Mark);
            var flags = part.Buildings.Select(n => parameters[BuildingParameterName(n)]).ToArray();
            string subject = string.Join(", ", flags.Select(f => f.Definition.Name));
            switch (part.Role)
            {
                case Role.Axis:
                    return Toggle("Оси", legacy: "Жук_Оси", order: 3);
                case Role.Geometry:
                    return flags.Length == 1 ? flags[0] : Toggle("Схема", legacy: "Жук_Контуры");
                case Role.Title:
                    return Toggle("Основная надпись");
                case Role.Text:
                    var labels = Toggle("Подписи", legacy: "Жук_Подписи");
                    if (flags.Length == 0) return labels;
                    if (part.Buildings.All(Composition.IsParking))
                        return Derived("Подпись " + subject, And(Ref(labels), Any(flags)));
                    var bodyLabels = parking.Length == 0 ? labels : Derived("Подписи корпусов",
                        And(Ref(labels), "not(" + Any(parking) + ")"));
                    if (part.Buildings.All(Composition.IsAnnex))
                    {
                        var annex = Toggle("Подписи пристроек");
                        bodyLabels = Derived("Видимость подписей пристроек", And(Ref(bodyLabels), Ref(annex)));
                    }
                    return Derived("Подпись " + subject, And(Ref(bodyLabels), Any(flags)));
                case Role.Hatch:
                    var hatch = Toggle("Штриховка", legacy: "Жук_Штриховки");
                    if (flags.Length == 0) return hatch;
                    var terms = new List<string> { Ref(hatch), Any(flags) };
                    if (parking.Length > 0 && !part.Buildings.All(Composition.IsParking))
                        terms.Add("not(" + Any(parking) + ")");
                    return Derived("Штриховка " + subject, And(terms.ToArray()));
                default:
                    return Toggle(SchemeDialog.RoleName(part.Role));
            }
        }

        private FamilyParameter Toggle(string name, bool initial = true, string legacy = null, int order = 4)
        {
            if (parameters.TryGetValue(name, out var existing))
            {
                if (!string.IsNullOrEmpty(existing.Formula))
                    throw new InvalidOperationException("Флажок «" + name + "» уже управляется формулой.");
                MoveToGroup(Validate(existing), GroupTypeId.Visibility);
                toggleOrder[existing.Id] = order;
                return existing;
            }

            FamilyParameter previous = null;
            if (legacy != null && parameters.TryGetValue(legacy, out var found) &&
                found.Definition.GetDataType() == SpecTypeId.Boolean.YesNo && string.IsNullOrEmpty(found.Formula))
                previous = found;
            var parameter = Add(name, GroupTypeId.Visibility);
            var current = manager.CurrentType;
            foreach (FamilyType type in manager.Types)
            {
                manager.CurrentType = type;
                manager.Set(parameter, (previous == null ? null : type.AsInteger(previous)) ?? (initial ? 1 : 0));
            }
            manager.CurrentType = current;
            toggleOrder[parameter.Id] = order;
            return parameter;
        }

        public void OrderToggles()
        {
            var current = manager.GetParameters();
            var ordered = current.Where(IsManagedToggle)
                .OrderBy(p => toggleOrder[p.Id])
                .ThenBy(p => p.Definition.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
            if (ordered.Length < 2) return;

            // Keep every other parameter in its original slot, including hidden parameters.
            int index = 0;
            var result = current.Select(p => IsManagedToggle(p) ? ordered[index++] : p).ToList();
            if (!current.Select(p => p.Id).SequenceEqual(result.Select(p => p.Id)))
                manager.ReorderParameters(result);

            bool IsManagedToggle(FamilyParameter p) =>
                toggleOrder.ContainsKey(p.Id) && p.Definition.GetGroupTypeId() == GroupTypeId.Visibility;
        }

        private FamilyParameter Derived(string name, string formula)
        {
            if (name.Length > 115) name = name.Substring(0, 94).TrimEnd() + " " + Hash(name);
            var parameter = parameters.TryGetValue(name, out var existing) ? Validate(existing) :
                Add(name, new ForgeTypeId());
            string current = formulas.TryGetValue(parameter, out var prepared) ? prepared : parameter.Formula;
            if (Normalize(current) != Normalize(formula))
            {
                if (existing != null && (!replacing || string.IsNullOrWhiteSpace(current) ||
                    ParkingIndependentFormula(current) != ParkingIndependentFormula(formula)))
                    throw new InvalidOperationException("Параметр «" + name + "» уже существует с другим выражением.");
            }
            formulas[parameter] = formula;
            MoveToGroup(parameter, new ForgeTypeId());
            return parameter;
        }

        public void ApplyInstanceParameters()
        {
            var pending = parameters.Values.Where(p => !p.IsInstance &&
                (toggleOrder.ContainsKey(p.Id) || formulas.ContainsKey(p) || IsPreviousCalculation(p))).ToList();
            var defaults = (from parameter in pending
                            where toggleOrder.ContainsKey(parameter.Id)
                            from type in manager.Types.Cast<FamilyType>()
                            let value = type.AsInteger(parameter)
                            where value.HasValue
                            select (Parameter: parameter, Type: type, Value: value.Value)).ToArray();

            // A type formula must become instance-based before its inputs can become instance-based.
            while (pending.Count > 0)
            {
                int count = pending.Count;
                Autodesk.Revit.Exceptions.InvalidOperationException failure = null;
                for (int i = pending.Count - 1; i >= 0; i--)
                {
                    try { manager.MakeInstance(pending[i]); }
                    catch (Autodesk.Revit.Exceptions.InvalidOperationException exception)
                    {
                        failure = exception;
                        continue;
                    }
                    pending.RemoveAt(i);
                }
                if (pending.Count == count)
                    throw new InvalidOperationException("Не удалось перевести параметры в экземпляр: " +
                        string.Join(", ", pending.Select(p => p.Definition.Name)) + ". " + failure?.Message, failure);
            }

            var current = manager.CurrentType;
            foreach (var entry in defaults)
            {
                manager.CurrentType = entry.Type;
                manager.Set(entry.Parameter, entry.Value);
            }
            manager.CurrentType = current;
            foreach (var entry in formulas)
                if (Normalize(entry.Key.Formula) != Normalize(entry.Value))
                    manager.SetFormula(entry.Key, entry.Value);
        }

        private bool IsPreviousCalculation(FamilyParameter parameter)
        {
            if (!replacing || parameter.IsShared || parameter.Definition.GetDataType() != SpecTypeId.Boolean.YesNo ||
                string.IsNullOrWhiteSpace(parameter.Formula)) return false;

            string name = parameter.Definition.Name;
            string formula = Normalize(parameter.Formula);
            return Regex.IsMatch(name, @"^Жук_видимость_[0-9A-F]{16}$") ||
                (name == "Подписи корпусов" && formula.StartsWith("and(Подписи,not(", StringComparison.Ordinal)) ||
                (name == "Видимость подписей пристроек" &&
                    (formula == "and(Подписикорпусов,Подписипристроек)" || formula == "and(Подписи,Подписипристроек)")) ||
                (name.StartsWith("Подпись ", StringComparison.Ordinal) &&
                    Regex.IsMatch(formula, @"^and\((?:Подписи|Подписикорпусов|Видимостьподписейпристроек),")) ||
                (name.StartsWith("Штриховка ", StringComparison.Ordinal) && formula.StartsWith("and(Штриховка,", StringComparison.Ordinal));
        }

        private void MoveToGroup(FamilyParameter parameter, ForgeTypeId group)
        {
            var definition = parameter.Definition;
            if (definition.GetGroupTypeId() == group) return;
            if (!parameter.IsShared && definition is InternalDefinition internalDefinition)
            {
                try { internalDefinition.SetGroupTypeId(group); }
                catch (Autodesk.Revit.Exceptions.ArgumentException) { }
                catch (Autodesk.Revit.Exceptions.InvalidOperationException) { }
            }
            if (parameter.Definition.GetGroupTypeId() != group) unmoved.Add(definition.Name);
        }

        private static string ParkingIndependentFormula(string formula)
        {
            const string parking = @"Паркинг(?:[+-]?\d+)?";
            return Regex.Replace(Normalize(formula),
                @",not\((?:or\(" + parking + "(?:," + parking + @")+\)|" + parking + @")\)", "")
                .Replace("Подписикорпусов", "Подписи");
        }

        public void RemoveUnusedLegacyParameters()
        {
            var candidates = parameters.Values.Where(p => !p.IsShared &&
                (Regex.IsMatch(p.Definition.Name, @"^Жук_видимость_[0-9A-F]{16}$") ||
                 p.Definition.Name.StartsWith("Жук_Постройка_", StringComparison.Ordinal) ||
                 new[] { "Жук_Контуры", "Жук_Оси", "Жук_Подписи", "Жук_Штриховки" }.Contains(p.Definition.Name)))
                .OrderBy(p => string.IsNullOrEmpty(p.Formula)).ToArray();
            foreach (var parameter in candidates)
            {
                var name = parameter.Definition.Name;
                if (parameter.AssociatedParameters.Size != 0 ||
                    parameters.Values.Any(other => other != parameter && other.Formula != null &&
                        Regex.IsMatch(other.Formula, @"(?<![\p{L}\p{Nd}_])" + Regex.Escape(name) + @"(?![\p{L}\p{Nd}_])")))
                    continue;
                manager.RemoveParameter(parameter);
                parameters.Remove(name);
            }
        }

        private FamilyParameter Add(string name, ForgeTypeId group)
        {
            var parameter = manager.AddParameter(name, group, SpecTypeId.Boolean.YesNo, true);
            parameters.Add(name, parameter);
            return parameter;
        }

        private static FamilyParameter Validate(FamilyParameter parameter)
        {
            if (parameter.Definition.GetDataType() != SpecTypeId.Boolean.YesNo)
                throw new InvalidOperationException("Параметр «" + parameter.Definition.Name + "» должен иметь тип Да/Нет.");
            return parameter;
        }

        private static string Normalize(string formula) => Regex.Replace(formula ?? "", @"[\s\[\]]", "");
        private static string Ref(FamilyParameter p) => "[" + p.Definition.Name + "]";
        private static string And(params string[] terms) => "and(" + string.Join(", ", terms) + ")";
        private static string Any(FamilyParameter[] values) =>
            values.Length == 1 ? Ref(values[0]) : "or(" + string.Join(", ", values.Select(Ref)) + ")";

        private static int Group(string name) => Composition.IsAnnex(name) ? 1 : Composition.IsParking(name) ? 2 : 0;

        internal static string BuildingParameterName(string name)
        {
            if (Composition.IsParking(name))
            {
                var level = Regex.Match(name, @"[−+-]?\d+");
                return level.Success ? "Паркинг " + level.Value.Replace('−', '-') : "Паркинг";
            }
            var safe = Regex.Replace(name, @"[^\p{L}\p{Nd} ._+-]", "_").Trim();
            return safe.Length <= 85 ? safe : safe.Substring(0, 65).TrimEnd() + " " + Hash(name);
        }

        private static string LegacyBuildingName(string name)
        {
            var safe = Regex.Replace(name.Replace("-", "_минус_").Replace("+", "_плюс_"), @"[^\p{L}\p{Nd}_]+", "_").Trim('_');
            if (safe.Length == 0) safe = Hash(name);
            if (safe.Length > 65) safe = safe.Substring(0, 65) + "_" + Hash(name);
            return "Жук_Постройка_" + safe;
        }

        internal static string Hash(string value)
        {
            using (var algorithm = SHA256.Create())
                return BitConverter.ToString(algorithm.ComputeHash(Encoding.UTF8.GetBytes(value)), 0, 8).Replace("-", "");
        }
    }
}
