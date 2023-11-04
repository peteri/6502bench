/*
 * Copyright 2019 faddenSoft
 * Copyright 2023 Peter Ibbotson
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 *     http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;

using Asm65;
using CommonUtil;
namespace SourceGen.AsmGen
{
    #region IGenerator

    /// <summary>
    /// Generate source code compatible with Microsoft Assembly Langugae Developement System Macro 80 assembler
    /// </summary>
    public class GenMacro80 : IGenerator
    {
        private const string ASM_FILE_SUFFIX = "_macro80.S";   // must start with underscore
        public DisasmProject Project { get; private set; }

        public Formatter SourceFormatter { get; private set; }

        public AppSettings Settings { get; private set; }

        public AssemblerQuirks Quirks { get; private set; }

        public LabelLocalizer Localizer { get { return mLocalizer; } }

        public int StartOffset { get { return 0; } }

        /// <summary>
        /// Working directory, i.e. where we write our output file(s).
        /// </summary>
        private string mWorkDirectory;

        /// <summary>
        /// If set, long labels get their own line.
        /// </summary>
        private bool mLongLabelNewLine;

        /// <summary>
        /// Output column widths.
        /// </summary>
        private int[] mColumnWidths;

        /// <summary>
        /// Base filename.  Typically the project file name without the ".dis65" extension.
        /// </summary>
        private string mFileNameBase;

        /// <summary>
        /// StringBuilder to use when composing a line.  Held here to reduce allocations.
        /// </summary>
        private StringBuilder mLineBuilder = new StringBuilder(100);

        /// <summary>
        /// Label localization helper.
        /// </summary>
        private LabelLocalizer mLocalizer;

        /// <summary>
        /// Stream to send the output to.
        /// </summary>
        private StreamWriter mOutStream;

        /// <summary>
        /// Address of next byte of output.
        /// </summary>
        private int mNextAddress = -1;

        /// <summary>
        /// True if we've seen an "is relative" flag in a block of address region start directives.
        /// </summary>
        /// <remarks>
        /// The trick with IsRelative is that, if there are multiple arstarts at the same
        /// offset, we need to output some or all of them, starting from the one just before
        /// the first IsRelative start.  We probably want to disable the use of Flush and
        /// just generate them as they appear, using the next Flush as the signal to return
        /// to standard behavior.
        /// </remarks>
        bool mIsInRelative = false;

        /// <summary>
        /// Holds detected version of configured assembler.
        /// </summary>
        private CommonUtil.Version mAsmVersion = CommonUtil.Version.NO_VERSION;

        // Interesting versions.
        private static CommonUtil.Version V3_43 = new CommonUtil.Version(3, 43);

        // Pseudo-op string constants.
        private static PseudoOp.PseudoOpNames sDataOpNames =
            new PseudoOp.PseudoOpNames(new Dictionary<string, string> {
                { "EquDirective", "equ" },
                { "VarDirective", "equ" },
                { "ArStartDirective", ".phase" },
                { "ArEndDirective", ".dephase" },
                //RegWidthDirective         // !al, !as, !rl, !rs
                //DataBankDirective
                { "DefineData1", "db" },
                { "DefineData2", "dw" },
                { "DefineData3", "db" },
                { "DefineData4", "db" },
                //DefineBigData2
                //DefineBigData3
                //DefineBigData4
                { "Fill", "ds" },
                { "Dense", "!hex" },
                { "Uninit", "!skip" },
                //Junk
                { "Align", "!align" },
                { "StrGeneric", "asc" },       // can use !xor for high ASCII
                //StrReverse
                //StrNullTerm
                //StrLen8
                //StrLen16
                { "StrDci", "dci" },
        });

        public void GetDefaultDisplayFormat(out PseudoOp.PseudoOpNames pseudoOps, out Formatter.FormatConfig formatConfig)
        {
            pseudoOps = sDataOpNames;

            formatConfig = new Formatter.FormatConfig();
            SetFormatConfigValues(ref formatConfig);
        }

        public void Configure(DisasmProject project, string workDirectory, string fileNameBase, AssemblerVersion asmVersion, AppSettings settings)
        {
            throw new NotImplementedException();
        }
        /// <summary>
        /// Configures the assembler-specific format items.
        /// </summary>
        private void SetFormatConfigValues(ref Formatter.FormatConfig config)
        {
            config.mOperandWrapLen = 64;
            config.mForceDirectOpcodeSuffix = string.Empty;
            config.mForceAbsOpcodeSuffix = ":";
            config.mForceLongOpcodeSuffix = "l";
            config.mForceDirectOperandPrefix = string.Empty;
            config.mForceAbsOperandPrefix = string.Empty;
            config.mForceLongOperandPrefix = string.Empty;
            config.mLocalVariableLabelPrefix = "]";
            config.mEndOfLineCommentDelimiter = ";";
            config.mFullLineCommentDelimiterBase = ";";
            config.mBoxLineCommentDelimiter = string.Empty;
            config.mNonUniqueLabelPrefix = ":";
            config.mCommaSeparatedDense = false;
            config.mExpressionMode = Formatter.FormatConfig.ExpressionMode.Merlin;

            Formatter.DelimiterSet charSet = new Formatter.DelimiterSet();
            charSet.Set(CharEncoding.Encoding.Ascii, Formatter.SINGLE_QUOTE_DELIM);
            charSet.Set(CharEncoding.Encoding.HighAscii, Formatter.DOUBLE_QUOTE_DELIM);
            config.mCharDelimiters = charSet;
        }
        public void FlushArDirectives()
        {
            throw new NotImplementedException();
        }

        public void GenerateShortSequence(int offset, int length, out string opcode, out string operand)
        {
            throw new NotImplementedException();
        }

        public GenerationResults GenerateSource(BackgroundWorker worker)
        {
            throw new NotImplementedException();
        }



        public FormatDescriptor ModifyInstructionOperandFormat(int offset, FormatDescriptor dfd, int operand)
        {
            throw new NotImplementedException();
        }

        public string ModifyOpcode(int offset, OpDef op)
        {
            throw new NotImplementedException();
        }

        public void OutputArDirective(AddressMap.AddressChange change)
        {
            throw new NotImplementedException();
        }

        public void OutputAsmConfig()
        {
            throw new NotImplementedException();
        }

        public void OutputDataOp(int offset)
        {
            throw new NotImplementedException();
        }

        public void OutputEquDirective(string name, string valueStr, string comment)
        {
            OutputLine(name, SourceFormatter.FormatPseudoOp(sDataOpNames.EquDirective),
                valueStr, SourceFormatter.FormatEolComment(comment));
        }

        public void OutputLine(string label, string opcode, string operand, string comment)
        {
            throw new NotImplementedException();
        }

        public void OutputLine(string fullLine)
        {
            throw new NotImplementedException();
        }

        public void OutputLocalVariableTable(int offset, List<DefSymbol> newDefs, LocalVariableTable allDefs)
        {
            throw new NotImplementedException();
        }

        public void OutputRegWidthDirective(int offset, int prevM, int prevX, int newM, int newX)
        {
            throw new NotImplementedException();
        }

        public void UpdateCharacterEncoding(FormatDescriptor dfd)
        {
            throw new NotImplementedException();
        }
    }

    #endregion


    #region IAssembler
    public class AsmMacro80 : IAssembler
    {
        // Paths from generator.
        private List<string> mPathNames;

        // Directory to make current before executing assembler.
        private string mWorkDirectory;

        public void GetExeIdentifiers(out string humanName, out string exeName)
        {
            humanName = "Macro-80 Assembler";
            exeName = "M80.COM";
        }

        public AssemblerConfig GetDefaultConfig()
        {
            return new AssemblerConfig(string.Empty, new int[] { 16, 6, 11, 74 });
        }

        public void Configure(GenerationResults results, string workDirectory)
        {
            // Clone pathNames, in case the caller decides to modify the original.
            mPathNames = CommonUtil.Container.CopyStringList(results.PathNames);
            mWorkDirectory = workDirectory;
        }

        public AssemblerVersion QueryVersion()
        {
            AssemblerConfig config =
                AssemblerConfig.GetConfig(AppSettings.Global, AssemblerInfo.Id.Macro80);
            if (config == null || string.IsNullOrEmpty(config.ExecutablePath))
            {
                return null;
            }
            // Okay can we find the assembler.
            if (!File.Exists(config.ExecutablePath))
                return null;
            // Read in to memory
            byte[] assemblerCode = File.ReadAllBytes(config.ExecutablePath);
            // Find the version number
            int versionStart = FindBytes(assemblerCode, "Macro-80 ");
            // Not macro-80 go home
            if (versionStart == -1)
                return null;
            // Get the version number (followed by date)
            var versionStr = "";
            while ((versionStr.Length < 10)
                && (versionStart < assemblerCode.Length)
                && (assemblerCode[versionStart] != 0x20))
                versionStr += (char)assemblerCode[versionStart++];
            // Are we 6502 capable?
            int pseudoOp6502 = FindBytes(assemblerCode, "6502");
            CommonUtil.Version version = CommonUtil.Version.Parse(versionStr);
            if ((!version.IsValid) || (pseudoOp6502 == -1))
            {
                return null;
            }
            return new AssemblerVersion(versionStr, version);
        }

        private int FindBytes(byte[] assemblerCode, string v)
        {
            throw new NotImplementedException();
        }

        public AssemblerResults RunAssembler(BackgroundWorker worker)
        {
            throw new NotImplementedException();
        }
    }
    #endregion
}

