﻿/*
 * Copyright 2019 faddenSoft
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
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;

using Asm65;

namespace SourceGen.WpfGui {
    /// <summary>
    /// Instruction operand editor.
    /// </summary>
    public partial class EditInstructionOperand : Window, INotifyPropertyChanged {
        /// <summary>
        /// Updated format descriptor.  Will be null if the user selected "default".
        /// </summary>
        public FormatDescriptor FormatDescriptorResult { get; private set; }

        private readonly string SYMBOL_NOT_USED;
        private readonly string SYMBOL_UNKNOWN;
        private readonly string SYMBOL_INVALID;

        /// <summary>
        /// Project reference.
        /// </summary>
        private DisasmProject mProject;

        /// <summary>
        /// Offset of instruction being edited.
        /// </summary>
        private int mOffset;

        /// <summary>
        /// Format object.
        /// </summary>
        private Formatter mFormatter;

        /// <summary>
        /// Operation definition, from file data.
        /// </summary>
        private OpDef mOpDef;

        /// <summary>
        /// Status flags at the point where the instruction is defined.  This tells us whether
        /// an operand is 8-bit or 16-bit.
        /// </summary>
        private StatusFlags mOpStatusFlags;

        /// <summary>
        /// Operand value, extracted from file data.  For a relative branch, this will be
        /// an address instead.
        /// </summary>
        private int mOperandValue;

        /// <summary>
        /// True when the input is valid.  Controls whether the OK button is enabled.
        /// </summary>
        public bool IsValid {
            get { return mIsValid; }
            set { mIsValid = value; OnPropertyChanged(); }
        }
        private bool mIsValid;

        /// <summary>
        /// Set when our load-time initialization is complete.
        /// </summary>
        private bool mLoadDone;

        // INotifyPropertyChanged implementation
        public event PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string propertyName = "") {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }


        /// <summary>
        /// Constructor.
        /// </summary>
        /// <param name="owner">Parent window.</param>
        /// <param name="project">Project reference.</param>
        /// <param name="offset">File offset of instruction start.</param>
        /// <param name="formatter">Formatter object, for preview window.</param>
        public EditInstructionOperand(Window owner, DisasmProject project, int offset,
                Formatter formatter) {
            InitializeComponent();
            Owner = owner;
            DataContext = this;

            mProject = project;
            mOffset = offset;
            mFormatter = formatter;

            SYMBOL_NOT_USED = (string)FindResource("str_SymbolNotUsed");
            SYMBOL_INVALID = (string)FindResource("str_SymbolNotValid");
            SYMBOL_UNKNOWN = (string)FindResource("str_SymbolUnknown");

            Debug.Assert(offset >= 0 && offset < project.FileDataLength);
            mOpDef = project.CpuDef.GetOpDef(project.FileData[offset]);
            Anattrib attr = project.GetAnattrib(offset);
            mOpStatusFlags = attr.StatusFlags;
            Debug.Assert(offset + mOpDef.GetLength(mOpStatusFlags) <= project.FileDataLength);

            if (attr.OperandAddress >= 0) {
                // Use this as the operand value when available.  This lets us present
                // relative branch instructions in the expected form.
                mOperandValue = attr.OperandAddress;
            } else {
                // For BlockMove this will have both parts.
                mOperandValue = mOpDef.GetOperand(project.FileData, offset, attr.StatusFlags);
            }
        }

        private void Window_Loaded(object sender, RoutedEventArgs e) {
            BasicFormat_Loaded();
            mLoadDone = true;
        }

        private void Window_ContentRendered(object sender, EventArgs e) {
            UpdateControls();
            symbolTextBox.SelectAll();
            symbolTextBox.Focus();
        }

        private void OkButton_Click(object sender, RoutedEventArgs e) {
            FormatDescriptorResult = CreateDescriptorFromControls();
            DialogResult = true;
        }

        /// <summary>
        /// Updates the state of the UI controls as the user interacts with the dialog.
        /// </summary>
        private void UpdateControls() {
            if (!mLoadDone) {
                return;
            }

            // Parts panel IsEnabled depends directly on formatSymbolButton.IsChecked.
            IsValid = true;
            IsSymbolAuto = false;
            SymbolValueDecimal = string.Empty;
            if (FormatSymbol) {
                if (!Asm65.Label.ValidateLabel(SymbolLabel)) {
                    SymbolValueHex = SYMBOL_INVALID;
                    IsValid = false;
                } else if (mProject.SymbolTable.TryGetValue(SymbolLabel, out Symbol sym)) {
                    if (sym.SymbolSource == Symbol.Source.Auto) {
                        // We try to block references to auto labels, but it's possible to get
                        // around it because FormatDescriptors are weak references (replace auto
                        // label with user label, reference non-existent auto label, remove user
                        // label).  We could try harder, but currently not necessary.
                        IsValid = false;
                        IsSymbolAuto = true;
                    }

                    SymbolValueHex = mFormatter.FormatHexValue(sym.Value, 4);
                    SymbolValueDecimal = mFormatter.FormatDecimalValue(sym.Value);
                } else {
                    // Valid but unknown symbol.  This is fine -- symbols don't have to exist.
                    SymbolValueHex = SYMBOL_UNKNOWN;
                }
            } else {
                SymbolValueHex = SYMBOL_NOT_USED;
            }

            UpdatePreview();
        }

        /// <summary>
        /// Updates the contents of the preview text box.
        /// </summary>
        private void UpdatePreview() {
            // Generate a descriptor from the controls.  This isn't strictly necessary, but it
            // gets all of the data in one small package.
            FormatDescriptor dfd = CreateDescriptorFromControls();

            if (dfd == null) {
                // Showing the right thing for the default format is surprisingly hard.  There
                // are a bunch of complicated steps that are performed in sequence, including
                // the "nearby label" lookups, the elision of hidden symbols, and other
                // obscure bits that may get tweaked from time to time.  These things are not
                // easy to factor out because we're slicing the data at a different angle: the
                // initial pass walks the entire file looking for one thing at a point before
                // analysis has completed, while here we're trying to mimic all of the
                // steps for a single offset, after analysis has finished.  It's a lot of work
                // to show text that they'll see as soon as they hit "OK".
                PreviewText = string.Empty;
                return;
            }

            StringBuilder sb = new StringBuilder(16);

            // Show the opcode.  Don't bother trying to figure out width disambiguation here.
            sb.Append(mFormatter.FormatOpcode(mOpDef, OpDef.WidthDisambiguation.None));
            sb.Append(' ');

            bool showHashPrefix = mOpDef.IsImmediate ||
                mOpDef.AddrMode == OpDef.AddressMode.BlockMove;
            if (showHashPrefix) {
                sb.Append('#');
            }

            Anattrib attr = mProject.GetAnattrib(mOffset);
            int previewHexDigits = (attr.Length - 1) * 2;
            int operandValue = mOperandValue;
            bool isPcRelative = false;
            bool isBlockMove = false;
            if (attr.OperandAddress >= 0) {
                if (mOpDef.AddrMode == OpDef.AddressMode.PCRel) {
                    previewHexDigits = 4;   // show branches as $xxxx even when on zero page
                    isPcRelative = true;
                } else if (mOpDef.AddrMode == OpDef.AddressMode.PCRelLong ||
                        mOpDef.AddrMode == OpDef.AddressMode.StackPCRelLong) {
                    isPcRelative = true;
                }
            } else {
                if (mOpDef.AddrMode == OpDef.AddressMode.BlockMove) {
                    // MVN and MVP screw things up by having two operands in one instruction.
                    // We deal with this by passing in the value from the second byte
                    // (source bank) as the value, and applying the chosen format to both bytes.
                    isBlockMove = true;
                    operandValue = mOperandValue >> 8;
                    previewHexDigits = 2;
                }
            }

            switch (dfd.FormatSubType) {
                case FormatDescriptor.SubType.Hex:
                    sb.Append(mFormatter.FormatHexValue(operandValue, previewHexDigits));
                    break;
                case FormatDescriptor.SubType.Decimal:
                    sb.Append(mFormatter.FormatDecimalValue(operandValue));
                    break;
                case FormatDescriptor.SubType.Binary:
                    sb.Append(mFormatter.FormatBinaryValue(operandValue, 8));
                    break;
                case FormatDescriptor.SubType.Ascii:
                case FormatDescriptor.SubType.HighAscii:
                case FormatDescriptor.SubType.C64Petscii:
                case FormatDescriptor.SubType.C64Screen:
                    CharEncoding.Encoding enc = PseudoOp.SubTypeToEnc(dfd.FormatSubType);
                    sb.Append(mFormatter.FormatCharacterValue(operandValue, enc));
                    break;
                case FormatDescriptor.SubType.Symbol:
                    if (mProject.SymbolTable.TryGetValue(dfd.SymbolRef.Label, out Symbol sym)) {
                        // Block move is a little weird.  "MVN label1,label2" is supposed to use
                        // the bank byte, while "MVN #const1,#const2" uses the entire symbol.
                        // The easiest thing to do is require the user to specify the "bank"
                        // part for 24-bit symbols, and always generate this as an immediate.
                        //
                        // MVN/MVP are also the only instructions with two operands, something
                        // we don't really handle.
                        // TODO(someday): allow a different symbol for each part of the operand.

                        // Hack to make relative branches look right in the preview window.
                        // Otherwise they show up like "<LABEL" because they appear to be
                        // only 8 bits.
                        int operandLen = dfd.Length - 1;
                        if (operandLen == 1 && isPcRelative) {
                            operandLen = 2;
                        }

                        // Set the operand length to 1 for block move so we use the part
                        // operators (<, >, ^) rather than bit-shifting.
                        if (isBlockMove) {
                            operandLen = 1;
                        }

                        PseudoOp.FormatNumericOpFlags flags;
                        if (isPcRelative) {
                            flags = PseudoOp.FormatNumericOpFlags.IsPcRel;
                        } else if (showHashPrefix) {
                            flags = PseudoOp.FormatNumericOpFlags.HasHashPrefix;
                        } else {
                            flags = PseudoOp.FormatNumericOpFlags.None;
                        }
                        string str = PseudoOp.FormatNumericOperand(mFormatter,
                            mProject.SymbolTable, null, dfd,
                            operandValue, operandLen, flags);
                        sb.Append(str);

                        if (sym.SymbolSource == Symbol.Source.Auto) {
                            mIsSymbolAuto = true;
                        }
                    } else {
                        sb.Append(dfd.SymbolRef.Label + " (?)");
                        Debug.Assert(!string.IsNullOrEmpty(dfd.SymbolRef.Label));
                        //symbolValueLabel.Text = Properties.Resources.MSG_SYMBOL_NOT_FOUND;
                    }
                    break;
                default:
                    Debug.Assert(false);
                    sb.Append("BUG");
                    break;
            }

            if (isBlockMove) {
                sb.Append(",#<dest>");
            }

            PreviewText = sb.ToString();
        }

        #region Basic Format

        public bool FormatDefault {
            get { return mFormatDefault; }
            set { mFormatDefault = value; OnPropertyChanged(); UpdateControls(); }
        }
        private bool mFormatDefault;

        public bool FormatHex {
            get { return mFormatHex; }
            set { mFormatHex = value; OnPropertyChanged(); UpdateControls(); }
        }
        private bool mFormatHex;

        public bool FormatDecimal {
            get { return mFormatDecimal; }
            set { mFormatDecimal = value; OnPropertyChanged(); UpdateControls(); }
        }
        private bool mFormatDecimal;

        public bool FormatBinary {
            get { return mFormatBinary; }
            set { mFormatBinary = value; OnPropertyChanged(); UpdateControls(); }
        }
        private bool mFormatBinary;

        public bool IsFormatAsciiAllowed {
            get { return mIsFormatAsciiAllowed; }
            set { mIsFormatAsciiAllowed = value; OnPropertyChanged(); }
        }
        private bool mIsFormatAsciiAllowed;

        public bool FormatAscii {
            get { return mFormatAscii; }
            set { mFormatAscii = value; OnPropertyChanged(); UpdateControls(); }
        }
        private bool mFormatAscii;

        public bool IsFormatPetsciiAllowed {
            get { return mIsFormatPetsciiAllowed; }
            set { mIsFormatPetsciiAllowed = value; OnPropertyChanged(); }
        }
        private bool mIsFormatPetsciiAllowed;

        public bool FormatPetscii {
            get { return mFormatPetscii; }
            set { mFormatPetscii = value; OnPropertyChanged(); UpdateControls(); }
        }
        private bool mFormatPetscii;

        public bool IsFormatScreenCodeAllowed {
            get { return mIsFormatScreenCodeAllowed; }
            set { mIsFormatScreenCodeAllowed = value; OnPropertyChanged(); }
        }
        private bool mIsFormatScreenCodeAllowed;

        public bool FormatScreenCode {
            get { return mFormatScreenCode; }
            set { mFormatScreenCode = value; OnPropertyChanged(); UpdateControls(); }
        }
        private bool mFormatScreenCode;

        public bool FormatSymbol {
            get { return mFormatSymbol; }
            set { mFormatSymbol = value; OnPropertyChanged(); UpdateControls(); }
        }
        private bool mFormatSymbol;

        public string SymbolLabel {
            get { return mSymbolLabel; }
            set {
                mSymbolLabel = value;
                OnPropertyChanged();
                // Set the radio button when the user starts typing.
                if (mLoadDone) {
                    FormatSymbol = true;
                }
                UpdateControls();
            }
        }
        private string mSymbolLabel;

        public bool IsSymbolAuto {
            get { return mIsSymbolAuto; }
            set { mIsSymbolAuto = value; OnPropertyChanged(); }
        }
        private bool mIsSymbolAuto;

        public string SymbolValueHex {
            get { return mSymbolValueHex; }
            set { mSymbolValueHex = value; OnPropertyChanged(); }
        }
        private string mSymbolValueHex;

        public string SymbolValueDecimal {
            get { return mSymbolValueDecimal; }
            set { mSymbolValueDecimal = value; OnPropertyChanged(); }
        }
        private string mSymbolValueDecimal;

        public bool FormatPartLow {
            get { return mFormatPartLow; }
            set { mFormatPartLow = value; OnPropertyChanged(); UpdateControls(); }
        }
        private bool mFormatPartLow;

        public bool FormatPartHigh {
            get { return mFormatPartHigh; }
            set { mFormatPartHigh = value; OnPropertyChanged(); UpdateControls(); }
        }
        private bool mFormatPartHigh;

        public bool FormatPartBank {
            get { return mFormatPartBank; }
            set { mFormatPartBank = value; OnPropertyChanged(); UpdateControls(); }
        }
        private bool mFormatPartBank;

        public string PreviewText {
            get { return mPreviewText; }
            set { mPreviewText = value; OnPropertyChanged(); }
        }
        private string mPreviewText;

        /// <summary>
        /// Configures the basic formatting options, based on the existing format descriptor.
        /// </summary>
        private void BasicFormat_Loaded() {
            // Can this be represented as a character?  We only allow the printable set
            // here, not the extended set (which includes control characters).
            if (mOperandValue == (byte) mOperandValue) {
                IsFormatAsciiAllowed =
                    CharEncoding.IsPrintableLowOrHighAscii((byte)mOperandValue);
                IsFormatPetsciiAllowed =
                    CharEncoding.IsPrintableC64Petscii((byte)mOperandValue);
                IsFormatScreenCodeAllowed =
                    CharEncoding.IsPrintableC64ScreenCode((byte)mOperandValue);
            } else {
                IsFormatAsciiAllowed = IsFormatPetsciiAllowed = IsFormatScreenCodeAllowed =
                    false;
            }

            SymbolLabel = string.Empty;
            FormatPartLow = true;       // could default to high for MVN/MVP
            FormatDefault = true;       // if nothing better comes along

            // Is there an operand format at this location?  If not, we're done.
            if (!mProject.OperandFormats.TryGetValue(mOffset, out FormatDescriptor dfd)) {
                return;
            }

            // NOTE: it's entirely possible to have a weird format (e.g. string) if the
            // instruction used to be hinted as data.  Handle it gracefully.
            switch (dfd.FormatType) {
                case FormatDescriptor.Type.NumericLE:
                    switch (dfd.FormatSubType) {
                        case FormatDescriptor.SubType.Hex:
                            FormatHex = true;
                            break;
                        case FormatDescriptor.SubType.Decimal:
                            FormatDecimal = true;
                            break;
                        case FormatDescriptor.SubType.Binary:
                            FormatBinary = true;
                            break;
                        case FormatDescriptor.SubType.Ascii:
                        case FormatDescriptor.SubType.HighAscii:
                            if (IsFormatAsciiAllowed) {
                                FormatAscii = true;
                            }
                            break;
                        case FormatDescriptor.SubType.C64Petscii:
                            if (IsFormatPetsciiAllowed) {
                                FormatPetscii = true;
                            }
                            break;
                        case FormatDescriptor.SubType.C64Screen:
                            if (IsFormatScreenCodeAllowed) {
                                FormatScreenCode = true;
                            }
                            break;
                        case FormatDescriptor.SubType.Symbol:
                            Debug.Assert(dfd.HasSymbol);
                            FormatSymbol = true;
                            switch (dfd.SymbolRef.ValuePart) {
                                case WeakSymbolRef.Part.Low:
                                    FormatPartLow = true;
                                    break;
                                case WeakSymbolRef.Part.High:
                                    FormatPartHigh = true;
                                    break;
                                case WeakSymbolRef.Part.Bank:
                                    FormatPartBank = true;
                                    break;
                                default:
                                    Debug.Assert(false);
                                    break;
                            }
                            SymbolLabel = dfd.SymbolRef.Label;
                            break;
                        case FormatDescriptor.SubType.None:
                        default:
                            break;
                    }
                    break;
                case FormatDescriptor.Type.NumericBE:
                case FormatDescriptor.Type.StringGeneric:
                case FormatDescriptor.Type.StringReverse:
                case FormatDescriptor.Type.StringNullTerm:
                case FormatDescriptor.Type.StringL8:
                case FormatDescriptor.Type.StringL16:
                case FormatDescriptor.Type.StringDci:
                case FormatDescriptor.Type.Dense:
                case FormatDescriptor.Type.Fill:
                default:
                    // Unexpected; used to be data?
                    break;
            }

            // In theory, if FormatDefault is still checked, we failed to find a useful match
            // for the format descriptor.  In practice, the radio button checkification stuff
            // happens later.  If we want to tell the user that there's a bad descriptor present,
            // we'll need to track it locally, or test all known radio buttons for True.
        }

        /// <summary>
        /// Creates a FormatDescriptor from the current state of the dialog controls.
        /// </summary>
        /// <returns>New FormatDescriptor.  Will return null if the default format is
        ///   selected, or symbol is selected with an empty label.</returns>
        private FormatDescriptor CreateDescriptorFromControls() {
            int instructionLength = mProject.GetAnattrib(mOffset).Length;

            if (FormatSymbol) {
                if (string.IsNullOrEmpty(SymbolLabel)) {
                    // empty symbol --> default format (intuitive way to delete label reference)
                    return null;
                }
                WeakSymbolRef.Part part;
                if (FormatPartLow) {
                    part = WeakSymbolRef.Part.Low;
                } else if (FormatPartHigh) {
                    part = WeakSymbolRef.Part.High;
                } else if (FormatPartBank) {
                    part = WeakSymbolRef.Part.Bank;
                } else {
                    Debug.Assert(false);
                    part = WeakSymbolRef.Part.Low;
                }
                return FormatDescriptor.Create(instructionLength,
                    new WeakSymbolRef(SymbolLabel, part), false);
            }

            FormatDescriptor.SubType subType;
            if (FormatDefault) {
                return null;
            } else if (FormatHex) {
                subType = FormatDescriptor.SubType.Hex;
            } else if (FormatDecimal) {
                subType = FormatDescriptor.SubType.Decimal;
            } else if (FormatBinary) {
                subType = FormatDescriptor.SubType.Binary;
            } else if (FormatAscii) {
                if (mOperandValue > 0x7f) {
                    subType = FormatDescriptor.SubType.HighAscii;
                } else {
                    subType = FormatDescriptor.SubType.Ascii;
                }
            } else if (FormatPetscii) {
                subType = FormatDescriptor.SubType.C64Petscii;
            } else if (FormatScreenCode) {
                subType = FormatDescriptor.SubType.C64Screen;
            } else {
                Debug.Assert(false);
                subType = FormatDescriptor.SubType.None;
            }

            return FormatDescriptor.Create(instructionLength,
                FormatDescriptor.Type.NumericLE, subType);
        }

        #endregion Basic Format

#if false
        /// <summary>
        /// Configures the buttons in the "symbol shortcuts" group box.  The entire box is
        /// disabled unless "symbol" is selected.  Other options are selectively enabled or
        /// disabled as appropriate for the current input.  If we disable the selection option,
        /// the selection will be reset to default.
        /// </summary>
        private void ConfigureSymbolShortcuts() {
            // operandOnlyRadioButton: always enabled
            // labelInsteadRadioButton: symbol is unknown and operand address has no label
            // operandAndLabelRadioButton: same as labelInstead
            // operandAndProjRadioButton: symbol is unknown and operand address is outside project

            string labelStr = symbolTextBox.Text;
            ShortcutArg = -1;

            // Is this a known symbol?  If so, disable most options and bail.
            if (mProject.SymbolTable.TryGetValue(labelStr, out Symbol sym)) {
                labelInsteadButton.IsEnabled = operandAndLabelButton.IsEnabled =
                    operandAndProjButton.IsEnabled = false;
                operandOnlyButton.IsChecked = true;
                return;
            }

            if (mAttr.OperandOffset >= 0) {
                // Operand target is inside the file.  Does the target offset already have a label?
                int targetOffset =
                    DataAnalysis.GetBaseOperandOffset(mProject, mAttr.OperandOffset);
                bool hasLabel = mProject.UserLabels.ContainsKey(targetOffset);
                labelInsteadButton.IsEnabled = operandAndLabelButton.IsEnabled =
                    !hasLabel;
                operandAndProjButton.IsEnabled = false;
                ShortcutArg = targetOffset;
            } else if (mAttr.OperandAddress >= 0) {
                // Operand target is outside the file.
                labelInsteadButton.IsEnabled = operandAndLabelButton.IsEnabled = false;
                operandAndProjButton.IsEnabled = true;
                ShortcutArg = mAttr.OperandAddress;
            } else {
                // Probably an immediate operand.
                // ?? Should operandAndProjButton be enabled for 8-bit constants?  We'd want
                //    to add it as a constant rather than an address.
                labelInsteadButton.IsEnabled = operandAndLabelButton.IsEnabled =
                    operandAndProjButton.IsEnabled = false;
            }

            // Select the default option if the currently-selected option is no longer available.
            if ((labelInsteadButton.IsChecked == true && labelInsteadButton.IsEnabled != true) ||
                    (operandAndLabelButton.IsChecked == true && !operandAndLabelButton.IsEnabled == true) ||
                    (operandAndProjButton.IsChecked == true && !operandAndProjButton.IsEnabled == true)) {
                operandOnlyButton.IsChecked = true;
            }
        }
#endif
    }
}
