﻿/* ****************************************************************************
 *
 * Copyright (c) Microsoft Corporation. 
 *
 * This source code is subject to terms and conditions of the Apache License, Version 2.0. A 
 * copy of the license can be found in the License.html file at the root of this distribution. If 
 * you cannot locate the Apache License, Version 2.0, please send an email to 
 * vspython@microsoft.com. By using this source code in any fashion, you are agreeing to be bound 
 * by the terms of the Apache License, Version 2.0.
 *
 * You must not remove this notice, or any other, from this software.
 *
 * ***************************************************************************/

using System.Linq;
using Microsoft.PythonTools.Parsing.Ast;

namespace Microsoft.PythonTools.Analysis.Values {
    /// <summary>
    /// Represents a list object with tracked type information.
    /// </summary>
    class ListInfo : SequenceInfo {
        private ListAppendBoundBuiltinMethodInfo _appendMethod;
        private ListPopBoundBuiltinMethodInfo _popMethod;
        private ListInsertBoundBuiltinMethodInfo _insertMethod;
        private ListExtendBoundBuiltinMethodInfo _extendMethod;

        public ListInfo(VariableDef[] indexTypes, BuiltinClassInfo seqType, Node node)
            : base(indexTypes, seqType, node) {
            EnsureAppend();
        }

        public override IAnalysisSet GetMember(Node node, AnalysisUnit unit, string name) {
            switch (name) {
                case "append":
                    EnsureAppend();
                    if (_appendMethod != null) {
                        return _appendMethod.SelfSet;
                    }
                    break;
                case "pop":
                    EnsurePop();
                    if (_popMethod != null) {
                        return _popMethod.SelfSet;
                    }
                    break;
                case "insert":
                    EnsureInsert();
                    if (_insertMethod != null) {
                        return _insertMethod.SelfSet;
                    }
                    break;
                case "extend":
                    EnsureExtend();
                    if (_extendMethod != null) {
                        return _extendMethod.SelfSet;
                    }
                    break;
            }

            return base.GetMember(node, unit, name);
        }

        internal void AppendItem(Node node, AnalysisUnit unit, IAnalysisSet set) {
            if (IndexTypes.Length == 0) {
                IndexTypes = new[] { new VariableDef() };
            }

            IndexTypes[0].MakeUnionStrongerIfMoreThan(ProjectState.Limits.IndexTypes, set);
            IndexTypes[0].AddTypes(unit, set);

            UnionType = null;
        }

        private void EnsureAppend() {
            if (_appendMethod == null) {
                IAnalysisSet value;
                if (TryGetMember("append", out value)) {
                    _appendMethod = new ListAppendBoundBuiltinMethodInfo(this, (BuiltinMethodInfo)value.First());
                }
            }
        }

        private void EnsurePop() {
            if (_popMethod == null) {
                IAnalysisSet value;
                if (TryGetMember("pop", out value)) {
                    _popMethod = new ListPopBoundBuiltinMethodInfo(this, (BuiltinMethodInfo)value.First());
                }
            }
        }

        private void EnsureInsert() {
            if (_insertMethod == null) {
                IAnalysisSet value;
                if (TryGetMember("insert", out value)) {
                    _insertMethod = new ListInsertBoundBuiltinMethodInfo(this, (BuiltinMethodInfo)value.First());
                }
            }
        }

        private void EnsureExtend() {
            if (_extendMethod == null) {
                IAnalysisSet value;
                if (TryGetMember("extend", out value)) {
                    _extendMethod = new ListExtendBoundBuiltinMethodInfo(this, (BuiltinMethodInfo)value.First());
                }
            }
        }
    }
}