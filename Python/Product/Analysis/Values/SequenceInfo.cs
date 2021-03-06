// Python Tools for Visual Studio
// Copyright(c) Microsoft Corporation
// All rights reserved.
//
// Licensed under the Apache License, Version 2.0 (the License); you may not use
// this file except in compliance with the License. You may obtain a copy of the
// License at http://www.apache.org/licenses/LICENSE-2.0
//
// THIS CODE IS PROVIDED ON AN  *AS IS* BASIS, WITHOUT WARRANTIES OR CONDITIONS
// OF ANY KIND, EITHER EXPRESS OR IMPLIED, INCLUDING WITHOUT LIMITATION ANY
// IMPLIED WARRANTIES OR CONDITIONS OF TITLE, FITNESS FOR A PARTICULAR PURPOSE,
// MERCHANTABLITY OR NON-INFRINGEMENT.
//
// See the Apache Version 2.0 License for specific language governing
// permissions and limitations under the License.

using System.Collections.Generic;
using System.Linq;
using Microsoft.PythonTools.Analysis.Analyzer;
using Microsoft.PythonTools.Interpreter;
using Microsoft.PythonTools.Parsing;
using Microsoft.PythonTools.Parsing.Ast;

namespace Microsoft.PythonTools.Analysis.Values {
    /// <summary>
    /// Specialized built-in instance for sequences (lists, tuples)
    /// </summary>
    internal class SequenceInfo : IterableInfo {
        private readonly ProjectEntry _declaringModule;
        private readonly int _declaringVersion;
        
        public SequenceInfo(VariableDef[] indexTypes, BuiltinClassInfo seqType, Node node, ProjectEntry entry)
            : base(indexTypes, seqType, node) {
            _declaringModule = entry;
            _declaringVersion = entry.AnalysisVersion;
        }

        public override IPythonProjectEntry DeclaringModule {
            get {
                return _declaringModule;
            }
        }

        public override int DeclaringVersion {
            get {
                return _declaringVersion;
            }
        }

        public override int? GetLength() {
            return IndexTypes.Length;
        }

        public override IAnalysisSet BinaryOperation(Node node, AnalysisUnit unit, PythonOperator operation, IAnalysisSet rhs) {
            SequenceInfo seq = null;
            VariableDef idx = null;
            var res = AnalysisSet.Empty;
            switch (operation) {
                case PythonOperator.Add:
                    foreach (var type in rhs.Where(t => !t.IsOfType(ClassInfo))) {
                        res = res.Union(CallReverseBinaryOp(node, unit, operation, rhs));
                    }
                    
                    foreach (var type in rhs.Where(t => t.IsOfType(ClassInfo))) {
                        if (seq == null) {
                            seq = (SequenceInfo)unit.Scope.GetOrMakeNodeValue(node,
                                NodeValueKind.Sequence,
                                _ => new SequenceInfo(new[] { new VariableDef() }, ClassInfo, node, unit.ProjectEntry)
                            );
                            idx = seq.IndexTypes[0];
                            idx.AddTypes(unit, GetEnumeratorTypes(node, unit));
                        }
                        idx.AddTypes(unit, type.GetEnumeratorTypes(node, unit));
                    }

                    if (seq != null) {
                        res = res.Union(seq);
                    }
                    break;
                case PythonOperator.Multiply:
                    foreach (var type in rhs) {
                        var typeId = type.TypeId;

                        if (typeId == BuiltinTypeId.Int || typeId == BuiltinTypeId.Long) {
                            res = res.Union(this);
                        } else {
                            res = res.Union(CallReverseBinaryOp(node, unit, operation, type));
                        }

                    }
                    break;
                default:
                    res = CallReverseBinaryOp(node, unit, operation, rhs);
                    break;
            }
            return res;
        }

        public override IAnalysisSet GetIndex(Node node, AnalysisUnit unit, IAnalysisSet index) {
            int? constIndex = GetConstantIndex(index);

            if (constIndex != null && constIndex.Value < IndexTypes.Length) {
                // TODO: Warn if outside known index and no appends?
                IndexTypes[constIndex.Value].AddDependency(unit);
                return IndexTypes[constIndex.Value].Types;
            }

            SliceInfo sliceInfo = GetSliceIndex(index);
            if (sliceInfo != null) {
                return this.SelfSet;
            }

            if (IndexTypes.Length == 0) {
                IndexTypes = new[] { new VariableDef() };
            }

            IndexTypes[0].AddDependency(unit);

            EnsureUnionType();
            return UnionType;
        }

        private SliceInfo GetSliceIndex(IAnalysisSet index) {
            foreach (var type in index) {
                if (type is SliceInfo) {
                    return type as SliceInfo;
                }
            }
            return null;
        }

        internal static int? GetConstantIndex(IAnalysisSet index) {
            int? constIndex = null;
            int typeCount = 0;
            foreach (var type in index) {
                object constValue = type.GetConstantValue();
                if (constValue != null && constValue is int) {
                    constIndex = (int)constValue;
                }

                typeCount++;
            }
            if (typeCount != 1) {
                constIndex = null;
            }
            return constIndex;
        }

        public override string ShortDescription {
            get {
                return _type.Name;
            }
        }

        public override string ToString() {
            return Description;
        }

        public override string Description {
            get {
                return MakeDescription(_type.Name);
            }
        }

        public override IEnumerable<KeyValuePair<IAnalysisSet, IAnalysisSet>> GetItems() {
            for (int i = 0; i < IndexTypes.Length; i++) {
                var value = IndexTypes[i];

                yield return new KeyValuePair<IAnalysisSet, IAnalysisSet>(
                    ProjectState.GetConstant(i),
                    value.Types
                );
            }
        }
    }

    internal class StarArgsSequenceInfo : SequenceInfo {
        public StarArgsSequenceInfo(VariableDef[] variableDef, BuiltinClassInfo builtinClassInfo, Node node, ProjectEntry entry)
            : base(variableDef, builtinClassInfo, node, entry) {
        }

        internal void SetIndex(AnalysisUnit unit, int index, IAnalysisSet value) {
            if (index >= IndexTypes.Length) {
                var newTypes = new VariableDef[index + 1];
                for (int i = 0; i < newTypes.Length; ++i) {
                    if (i < IndexTypes.Length) {
                        newTypes[i] = IndexTypes[i];
                    } else {
                        newTypes[i] = new VariableDef();
                    }
                }
                IndexTypes = newTypes;
            }
            IndexTypes[index].AddTypes(unit, value);
        }

        public override void SetIndex(Node node, AnalysisUnit unit, IAnalysisSet index, IAnalysisSet value) {
            int? constIndex = GetConstantIndex(index);

            if (constIndex != null) {
                SetIndex(unit, constIndex.Value, value);
            } else {
                if (IndexTypes.Length == 0) {
                    IndexTypes = new[] { new VariableDef() };
                }
                IndexTypes[0].AddTypes(unit, value);
            }
        }


        public override string ShortDescription {
            get {
                return base.ShortDescription;
            }
        }

        public override string ToString() {
            return "*" + base.ToString();
        }

        internal int TypesCount {
            get {
                if (IndexTypes == null) {
                    return 0;
                }
                return IndexTypes.Aggregate(0, (total, it) => total + it.TypesNoCopy.Count);
            }
        }

        internal void MakeUnionStronger() {
            if (IndexTypes != null) {
                foreach (var it in IndexTypes) {
                    it.MakeUnionStronger();
                }
            }
        }

        internal bool MakeUnionStrongerIfMoreThan(int typeCount, IAnalysisSet extraTypes = null) {
            bool anyChanged = false;
            if (IndexTypes != null) {
                foreach (var it in IndexTypes) {
                    anyChanged |= it.MakeUnionStrongerIfMoreThan(typeCount, extraTypes);
                }
            }
            return anyChanged;
        }
    }

    /// <summary>
    /// Represents a *args parameter for a function definition.  Holds onto a SequenceInfo which
    /// includes all of the types passed in via splatting or extra position arguments.
    /// </summary>
    sealed class ListParameterVariableDef : LocatedVariableDef {
        public readonly StarArgsSequenceInfo List;

        public ListParameterVariableDef(AnalysisUnit unit, Node location)
            : base(unit.DeclaringModule.ProjectEntry, location) {
            List = new StarArgsSequenceInfo(
                VariableDef.EmptyArray,
                unit.ProjectState.ClassInfos[BuiltinTypeId.Tuple],
                location,
                unit.ProjectEntry
            );
            base.AddTypes(unit, List);
        }

        public ListParameterVariableDef(AnalysisUnit unit, Node location, VariableDef copy)
            : base(unit.DeclaringModule.ProjectEntry, location, copy) {
            List = new StarArgsSequenceInfo(
                VariableDef.EmptyArray,
                unit.ProjectState.ClassInfos[BuiltinTypeId.Tuple],
                location,
                unit.ProjectEntry
            );
            base.AddTypes(unit, List);
        }
    }

}
