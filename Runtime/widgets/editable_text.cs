﻿using System;
using System.Collections.Generic;
using Unity.UIWidgets.animation;
using Unity.UIWidgets.async;
using Unity.UIWidgets.foundation;
using Unity.UIWidgets.painting;
using Unity.UIWidgets.rendering;
using Unity.UIWidgets.scheduler;
using Unity.UIWidgets.service;
using Unity.UIWidgets.ui;
using TextStyle = Unity.UIWidgets.painting.TextStyle;

namespace Unity.UIWidgets.widgets {
    public delegate void SelectionChangedCallback(TextSelection selection, SelectionChangedCause cause);

    public class TextEditingController : ValueNotifier<TextEditingValue> {
        public TextEditingController(string text) : base(text == null
            ? TextEditingValue.empty
            : new TextEditingValue(text)) {
        }

        TextEditingController(TextEditingValue value) : base(value ?? TextEditingValue.empty) {
        }

        public TextEditingController fromValue(TextEditingValue value) {
            return new TextEditingController(value);
        }

        public string text {
            get { return this.value.text; }

            set {
                this.value = this.value.copyWith(text: value, selection: TextSelection.collapsed(-1),
                    composing: TextRange.empty);
            }
        }

        public TextSelection selection {
            get { return this.value.selection; }

            set {
                if (value.start > this.text.Length || value.end > this.text.Length) {
                    throw new UIWidgetsError($"invalid text selection: {value}");
                }

                this.value = this.value.copyWith(selection: value, composing: TextRange.empty);
            }
        }

        public void clear() {
            this.value = TextEditingValue.empty;
        }

        public void clearComposing() {
            this.value = this.value.copyWith(composing: TextRange.empty);
        }
    }

    public class EditableText : StatefulWidget {
        public readonly TextEditingController controller;

        public readonly FocusNode focusNode;

        public readonly bool obscureText;

        public readonly bool autocorrect;

        public readonly TextStyle style;

        public readonly TextAlign textAlign;

        public readonly TextDirection? textDirection;

        public readonly double textScaleFactor;

        public readonly Color cursorColor;

        public readonly int maxLines;

        public readonly bool autofocus;

        public readonly Color selectionColor;

        public readonly ValueChanged<string> onChanged;
        public readonly ValueChanged<string> onSubmitted;

        public readonly SelectionChangedCallback onSelectionChanged;

        public readonly List<TextInputFormatter> inputFormatters;

        public readonly bool rendererIgnoresPointer;

        public EditableText(TextEditingController controller, FocusNode focusNode, TextStyle style,
            Color cursorColor, bool obscureText = false, bool autocorrect = false,
            TextAlign textAlign = TextAlign.left, TextDirection? textDirection = null,
            double textScaleFactor = 1.0, int maxLines = 1,
            bool autofocus = false, Color selectionColor = null, ValueChanged<string> onChanged = null,
            ValueChanged<string> onSubmitted = null, SelectionChangedCallback onSelectionChanged = null,
            List<TextInputFormatter> inputFormatters = null, bool rendererIgnoresPointer = false,
            EdgeInsets scrollPadding = null,
            Key key = null) : base(key) {
            D.assert(controller != null);
            D.assert(focusNode != null);
            D.assert(style != null);
            D.assert(cursorColor != null);
            this.scrollPadding = scrollPadding ?? EdgeInsets.all(20.0);
            this.controller = controller;
            this.focusNode = focusNode;
            this.obscureText = obscureText;
            this.autocorrect = autocorrect;
            this.style = style;
            this.textAlign = textAlign;
            this.textDirection = textDirection;
            this.textScaleFactor = textScaleFactor;
            this.cursorColor = cursorColor;
            this.maxLines = maxLines;
            this.autofocus = autofocus;
            this.selectionColor = selectionColor;
            this.onChanged = onChanged;
            this.onSubmitted = onSubmitted;
            this.onSelectionChanged = onSelectionChanged;
            this.rendererIgnoresPointer = rendererIgnoresPointer;
            if (maxLines == 1) {
                this.inputFormatters = new List<TextInputFormatter>();
                this.inputFormatters.Add(BlacklistingTextInputFormatter.singleLineFormatter);
                if (inputFormatters != null) {
                    this.inputFormatters.AddRange(inputFormatters);
                }
            } else {
                this.inputFormatters = inputFormatters;
            }
        }

        public readonly EdgeInsets scrollPadding;

        public override State createState() {
            return new EditableTextState();
        }

        public override void debugFillProperties(DiagnosticPropertiesBuilder properties) {
            base.debugFillProperties(properties);
            properties.add(new DiagnosticsProperty<TextEditingController>("controller", this.controller));
            properties.add(new DiagnosticsProperty<FocusNode>("focusNode", this.focusNode));
            properties.add(new DiagnosticsProperty<bool>("obscureText", this.obscureText, defaultValue: false));
            properties.add(new DiagnosticsProperty<bool>("autocorrect", this.autocorrect, defaultValue: true));
            if (this.style != null) {
                this.style.debugFillProperties(properties);
            }

            properties.add(new EnumProperty<TextAlign>("textAlign", this.textAlign,
                defaultValue: Diagnostics.kNullDefaultValue));
            properties.add(new EnumProperty<TextDirection?>("textDirection", this.textDirection,
                defaultValue: Diagnostics.kNullDefaultValue));
            properties.add(new DiagnosticsProperty<double>("textScaleFactor", this.textScaleFactor,
                defaultValue: Diagnostics.kNullDefaultValue));
            properties.add(new DiagnosticsProperty<int>("maxLines", this.maxLines, defaultValue: 1));
            properties.add(new DiagnosticsProperty<bool>("autofocus", this.autofocus, defaultValue: false));
        }
    }

    public class EditableTextState : AutomaticKeepAliveClientMixin<EditableText>, TextInputClient {
        const int _kObscureShowLatestCharCursorTicks = 3;
        static TimeSpan _kCursorBlinkHalfPeriod = TimeSpan.FromMilliseconds(500);
        Timer _cursorTimer;
        ValueNotifier<bool> _showCursor = new ValueNotifier<bool>(false);
        GlobalKey _editableKey = GlobalKey.key();
        bool _didAutoFocus = false;
        public ScrollController _scrollController = new ScrollController();

        TextInputConnection _textInputConnection;
        int _obscureShowCharTicksPending = 0;
        int _obscureLatestCharIndex;

        bool _textChangedSinceLastCaretUpdate = false;

        protected override bool wantKeepAlive {
            get { return this.widget.focusNode.hasFocus; }
        }

        public override void initState() {
            base.initState();
            this.widget.controller.addListener(this._didChangeTextEditingValue);
            this.widget.focusNode.addListener(this._handleFocusChanged);
        }

        public override void didChangeDependencies() {
            base.didChangeDependencies();
            if (!this._didAutoFocus && this.widget.autofocus) {
                FocusScope.of(this.context).autofocus(this.widget.focusNode);
                this._didAutoFocus = true;
            }
        }

        public override void didUpdateWidget(StatefulWidget old) {
            EditableText oldWidget = (EditableText) old;
            base.didUpdateWidget(oldWidget);
            if (this.widget.controller != oldWidget.controller) {
                oldWidget.controller.removeListener(this._didChangeTextEditingValue);
                this.widget.controller.addListener(this._didChangeTextEditingValue);
                this._updateRemoteEditingValueIfNeeded();
            }

            if (this.widget.focusNode != oldWidget.focusNode) {
                oldWidget.focusNode.removeListener(this._handleFocusChanged);
                this.widget.focusNode.addListener(this._handleFocusChanged);
                this.updateKeepAlive();
            }
        }

        public override void dispose() {
            this.widget.controller.removeListener(this._didChangeTextEditingValue);
            this._closeInputConnectionIfNeeded();
            D.assert(!this._hasInputConnection);
            this._stopCursorTimer();
            D.assert(this._cursorTimer == null);
            this.widget.focusNode.removeListener(this._handleFocusChanged);
            base.dispose();
        }

        TextEditingValue _lastKnownRemoteTextEditingValue;

        public void updateEditingValue(TextEditingValue value) {
            if (value.text != this._value.text) {
                // _hideSelectionOverlayIfNeeded();
                if (this.widget.obscureText && value.text.Length == this._value.text.Length + 1) {
                    this._obscureShowCharTicksPending = _kObscureShowLatestCharCursorTicks;
                    this._obscureLatestCharIndex = this._value.selection.baseOffset;
                }
            }

            this._lastKnownRemoteTextEditingValue = value;
            this._formatAndSetValue(value);
        }

        public TextEditingValue getValueForOperation(TextEditOp operation) {
            TextPosition newPosition = null;
            TextPosition newExtend = null;
            TextEditingValue newValue = null;
            TextSelection newSelection = null;
            TextPosition startPos = new TextPosition(this._value.selection.start, this._value.selection.affinity);
            switch (operation) {
                case TextEditOp.MoveLeft:
                    newValue = this._value.moveLeft();
                    break;
                case TextEditOp.MoveRight:
                    newValue = this._value.moveRight();
                    break;
                case TextEditOp.MoveUp:
                    newPosition = this.renderEditable.getPositionUp(startPos);
                    break;
                case TextEditOp.MoveDown:
                    newPosition = this.renderEditable.getPositionDown(startPos);
                    break;
                case TextEditOp.MoveLineStart:
                    newPosition = this.renderEditable.getParagraphStart(startPos, TextAffinity.downstream);
                    break;
                case TextEditOp.MoveLineEnd:
                    newPosition = this.renderEditable.getParagraphEnd(startPos, TextAffinity.upstream);
                    break;
                case TextEditOp.MoveWordRight:
                    newPosition = this.renderEditable.getWordRight(startPos);
                    break;
                case TextEditOp.MoveWordLeft:
                    newPosition = this.renderEditable.getWordLeft(startPos);
                    break;
//                case TextEditOp.MoveToStartOfNextWord:      MoveToStartOfNextWord(); break;
//                case TextEditOp.MoveToEndOfPreviousWord:        MoveToEndOfPreviousWord(); break;
                case TextEditOp.MoveTextStart:
                    newPosition = new TextPosition(0);
                    break;
                case TextEditOp.MoveTextEnd:
                    newPosition = new TextPosition(this._value.text.Length);
                    break;
                case TextEditOp.MoveParagraphForward:
                    newPosition = this.renderEditable.getParagraphForward(startPos);
                    break;
                case TextEditOp.MoveParagraphBackward:
                    newPosition = this.renderEditable.getParagraphBackward(startPos);
                    break;
                case TextEditOp.MoveGraphicalLineStart:
                    newPosition = this.renderEditable.getLineStartPosition(startPos, TextAffinity.downstream);
                    break;
                case TextEditOp.MoveGraphicalLineEnd:
                    newPosition = this.renderEditable.getLineEndPosition(startPos, TextAffinity.upstream);
                    break;
                case TextEditOp.SelectLeft:
                    newValue = this._value.extendLeft();
                    break;
                case TextEditOp.SelectRight:
                    newValue = this._value.extendRight();
                    break;
                case TextEditOp.SelectUp:
                    newExtend = this.renderEditable.getPositionUp(this._value.selection.extendPos);
                    break;
                case TextEditOp.SelectDown:
                    newExtend = this.renderEditable.getPositionDown(this._value.selection.extendPos);
                    break;
                case TextEditOp.SelectWordRight:
                    newExtend = this.renderEditable.getWordRight(this._value.selection.extendPos);
                    break;
                case TextEditOp.SelectWordLeft:
                    newExtend = this.renderEditable.getWordLeft(this._value.selection.extendPos);
                    break;
//                case TextEditOp.SelectToEndOfPreviousWord:  SelectToEndOfPreviousWord(); break;
//                case TextEditOp.SelectToStartOfNextWord:    SelectToStartOfNextWord(); break;
//
                case TextEditOp.SelectTextStart:
                    newExtend = new TextPosition(0);
                    break;
                case TextEditOp.SelectTextEnd:
                    newExtend = new TextPosition(this._value.text.Length);
                    break;
                case TextEditOp.ExpandSelectGraphicalLineStart:
                    if (this._value.selection.isCollapsed ||
                        !this.renderEditable.isLineEndOrStart(this._value.selection.start)) {
                        newSelection = new TextSelection(this.renderEditable.getLineStartPosition(startPos).offset,
                            this._value.selection.end, this._value.selection.affinity);
                    }

                    break;
                case TextEditOp.ExpandSelectGraphicalLineEnd:
                    if (this._value.selection.isCollapsed ||
                        !this.renderEditable.isLineEndOrStart(this._value.selection.end)) {
                        newSelection = new TextSelection(this._value.selection.start,
                            this.renderEditable.getLineEndPosition(this._value.selection.endPos).offset,
                            this._value.selection.affinity);
                    }
                    break;
                case TextEditOp.SelectParagraphForward:
                    newExtend = this.renderEditable.getParagraphForward(this._value.selection.extendPos);
                    break;
                case TextEditOp.SelectParagraphBackward:
                    newExtend = this.renderEditable.getParagraphBackward(this._value.selection.extendPos);
                    break;
                case TextEditOp.SelectGraphicalLineStart:
                    newExtend = this.renderEditable.getLineStartPosition(this._value.selection.extendPos);
                    break;
                case TextEditOp.SelectGraphicalLineEnd:
                    newExtend = this.renderEditable.getLineEndPosition(startPos);
                    break;
                case TextEditOp.Delete:
                    newValue = this._value.deleteSelection(false);
                    break;
                case TextEditOp.Backspace:
                    newValue = this._value.deleteSelection();
                    break;
                case TextEditOp.SelectAll:
                    newSelection = this._value.selection.copyWith(baseOffset: 0, extentOffset: this._value.text.Length);
                    break;
            }

            if (newPosition != null) {
                return this._value.copyWith(selection: TextSelection.fromPosition(newPosition));
            } else if (newExtend != null) {
                return this._value.copyWith(selection: this._value.selection.copyWith(extentOffset: newExtend.offset));
            } else if (newSelection != null) {
                return this._value.copyWith(selection: newSelection);
            } else if (newValue != null) {
                return newValue;
            }

            return this._value;
        }

        void _updateRemoteEditingValueIfNeeded() {
            if (!this._hasInputConnection) {
                return;
            }
            var localValue = this._value;
            if (localValue == this._lastKnownRemoteTextEditingValue) {
                return;
            }
            this._lastKnownRemoteTextEditingValue = localValue;
            this._textInputConnection.setEditingState(localValue);
        }

        // Calculate the new scroll offset so the cursor remains visible.
        double _getScrollOffsetForCaret(Rect caretRect) {
            double caretStart = this._isMultiline ? caretRect.top : caretRect.left;
            double caretEnd = this._isMultiline ? caretRect.bottom : caretRect.right;
            double scrollOffset = this._scrollController.offset;
            double viewportExtent = this._scrollController.position.viewportDimension;
            if (caretStart < 0.0) {
                scrollOffset += caretStart;
            } else if (caretEnd >= viewportExtent) {
                scrollOffset += caretEnd - viewportExtent;
            }

            return scrollOffset;
        }

        // Calculates where the `caretRect` would be if `_scrollController.offset` is set to `scrollOffset`.
        Rect _getCaretRectAtScrollOffset(Rect caretRect, double scrollOffset) {
            double offsetDiff = this._scrollController.offset - scrollOffset;
            return this._isMultiline ? caretRect.translate(0.0, offsetDiff) : caretRect.translate(offsetDiff, 0.0);
        }

        bool _hasInputConnection {
            get { return this._textInputConnection != null && this._textInputConnection.attached; }
        }


        void _openInputConnection() {
            if (!this._hasInputConnection) {
                TextEditingValue localValue = this._value;
                this._lastKnownRemoteTextEditingValue = localValue;
                this._textInputConnection = Window.instance.textInput.attach(this);
                this._textInputConnection.setEditingState(localValue);
            }
        }

        void _closeInputConnectionIfNeeded() {
            if (this._hasInputConnection) {
                this._textInputConnection.close();
                this._textInputConnection = null;
                this._lastKnownRemoteTextEditingValue = null;
            }
        }

        void _openOrCloseInputConnectionIfNeeded() {
            if (this._hasFocus && this.widget.focusNode.consumeKeyboardToken()) {
                this._openInputConnection();
            } else if (!this._hasFocus) {
                this._closeInputConnectionIfNeeded();
                this.widget.controller.clearComposing();
            }
        }

        public void requestKeyboard() {
            if (this._hasFocus) {
                this._openInputConnection();
            } else {
                FocusScope.of(this.context).requestFocus(this.widget.focusNode);
            }
        }

        void _handleSelectionChanged(TextSelection selection, RenderEditable renderObject,
            SelectionChangedCause cause) {
            this.widget.controller.selection = selection;
            this.requestKeyboard();

            if (this.widget.onSelectionChanged != null) {
                this.widget.onSelectionChanged(selection, cause);
            }
        }

        Rect _currentCaretRect;

        void _handleCaretChanged(Rect caretRect) {
            this._currentCaretRect = caretRect;
            // If the caret location has changed due to an update to the text or
            // selection, then scroll the caret into view.
            if (this._textChangedSinceLastCaretUpdate) {
                this._textChangedSinceLastCaretUpdate = false;
                this._showCaretOnScreen();
            }
        }

        // Animation configuration for scrolling the caret back on screen.
        static readonly TimeSpan _caretAnimationDuration = TimeSpan.FromMilliseconds(100);
        static readonly Curve _caretAnimationCurve = Curves.fastOutSlowIn;
        bool _showCaretOnScreenScheduled = false;

        void _showCaretOnScreen() {
            if (this._showCaretOnScreenScheduled) {
                return;
            }

            this._showCaretOnScreenScheduled = true;
            SchedulerBinding.instance.addPostFrameCallback(_ => {
                this._showCaretOnScreenScheduled = false;
                if (this._currentCaretRect == null || !this._scrollController.hasClients) {
                    return;
                }
                double scrollOffsetForCaret = this._getScrollOffsetForCaret(this._currentCaretRect);
                this._scrollController.animateTo(scrollOffsetForCaret,
                    duration: _caretAnimationDuration,
                    curve: _caretAnimationCurve);

                Rect newCaretRect = this._getCaretRectAtScrollOffset(this._currentCaretRect, scrollOffsetForCaret);
                // Enlarge newCaretRect by scrollPadding to ensure that caret is not positioned directly at the edge after scrolling.
                Rect inflatedRect = Rect.fromLTRB(
                    newCaretRect.left - this.widget.scrollPadding.left,
                    newCaretRect.top - this.widget.scrollPadding.top,
                    newCaretRect.right + this.widget.scrollPadding.right,
                    newCaretRect.bottom + this.widget.scrollPadding.bottom
                );
                this._editableKey.currentContext.findRenderObject().showOnScreen(
                    rect: inflatedRect,
                    duration: _caretAnimationDuration,
                    curve: _caretAnimationCurve
                );
            });
        }

        void _formatAndSetValue(TextEditingValue value) {
            var textChanged = (this._value == null ? null : this._value.text) != (value == null ? null : value.text);
            if (this.widget.inputFormatters != null && this.widget.inputFormatters.isNotEmpty()) {
                foreach (var formatter in this.widget.inputFormatters) {
                    value = formatter.formatEditUpdate(this._value, value);
                }

                this._value = value;
                this._updateRemoteEditingValueIfNeeded();
            } else {
                this._value = value;
            }

            if (textChanged && this.widget.onChanged != null) {
                this.widget.onChanged(value.text);
            }
        }

        public bool cursorCurrentlyVisible {
            get { return this._showCursor.value; }
        }

        public TimeSpan cursorBlinkInterval {
            get { return _kCursorBlinkHalfPeriod; }
        }

        void _cursorTick() {
            this._showCursor.value = !this._showCursor.value;
            if (this._obscureShowCharTicksPending > 0) {
                this.setState(() => { this._obscureShowCharTicksPending--; });
            }
        }

        void _startCursorTimer() {
            this._showCursor.value = true;
            this._cursorTimer = Window.instance.run(_kCursorBlinkHalfPeriod, this._cursorTick,
                periodic: true);
        }

        void _stopCursorTimer() {
            if (this._cursorTimer != null) {
                this._cursorTimer.cancel();
            }
            this._cursorTimer = null;
            this._showCursor.value = false;
            this._obscureShowCharTicksPending = 0;
        }

        void _startOrStopCursorTimerIfNeeded() {
            if (this._cursorTimer == null && this._hasFocus && this._value.selection.isCollapsed) {
                this._startCursorTimer();
            } else if (this._cursorTimer != null && (!this._hasFocus || !this._value.selection.isCollapsed)) {
                this._stopCursorTimer();
            }
        }

        TextEditingValue _value {
            get { return this.widget.controller.value; }
            set { this.widget.controller.value = value; }
        }

        bool _hasFocus {
            get { return this.widget.focusNode.hasFocus; }
        }

        bool _isMultiline {
            get { return this.widget.maxLines != 1; }
        }

        void _didChangeTextEditingValue() {
            this._updateRemoteEditingValueIfNeeded();
            this._startOrStopCursorTimerIfNeeded();
            this._textChangedSinceLastCaretUpdate = true;
            this.setState(() => { });
        }

        void _handleFocusChanged() {
            this._openOrCloseInputConnectionIfNeeded();
            this._startOrStopCursorTimerIfNeeded();
            if (!this._hasFocus) {
                this._value = new TextEditingValue(text: this._value.text);
            } else if (!this._value.selection.isValid) {
                this.widget.controller.selection = TextSelection.collapsed(offset: this._value.text.Length);
            }

            this.updateKeepAlive();
        }


        TextDirection? _textDirection {
            get {
                TextDirection? result = this.widget.textDirection ?? Directionality.of(this.context);
                D.assert(result != null,
                    $"{this.GetType().FullName} created without a textDirection and with no ambient Directionality.");
                return result;
            }
        }

        public RenderEditable renderEditable {
            get { return (RenderEditable) this._editableKey.currentContext.findRenderObject(); }
        }

        public override Widget build(BuildContext context) {
            FocusScope.of(context).reparentIfNeeded(this.widget.focusNode);
            base.build(context); // See AutomaticKeepAliveClientMixin.

            return new Scrollable(
                axisDirection: this._isMultiline ? AxisDirection.down : AxisDirection.right,
                controller: this._scrollController,
                physics: new ClampingScrollPhysics(),
                viewportBuilder: (BuildContext _context, ViewportOffset offset) =>
                    new _Editable(
                        key: this._editableKey,
                        textSpan: this.buildTextSpan(),
                        value: this._value,
                        cursorColor: this.widget.cursorColor,
                        showCursor: this._showCursor,
                        hasFocus: this._hasFocus,
                        maxLines: this.widget.maxLines,
                        selectionColor: this.widget.selectionColor,
                        textScaleFactor: Window.instance
                            .devicePixelRatio, // todo widget.textScaleFactor ?? MediaQuery.textScaleFactorOf(context),
                        textAlign: this.widget.textAlign,
                        textDirection: this._textDirection,
                        obscureText: this.widget.obscureText,
                        autocorrect: this.widget.autocorrect,
                        offset: offset,
                        onSelectionChanged: this._handleSelectionChanged,
                        onCaretChanged: this._handleCaretChanged,
                        rendererIgnoresPointer: this.widget.rendererIgnoresPointer
                    )
            );
        }

        public TextSpan buildTextSpan() {
            if (!this.widget.obscureText && this._value.composing.isValid) {
                TextStyle composingStyle = this.widget.style.merge(
                    new TextStyle(decoration: TextDecoration.underline)
                );

                return new TextSpan(
                    style: this.widget.style,
                    children: new List<TextSpan> {
                        new TextSpan(text: this._value.composing.textBefore(this._value.text)),
                        new TextSpan(
                            style: composingStyle,
                            text: this._value.composing.textInside(this._value.text)
                        ),
                        new TextSpan(text: this._value.composing.textAfter(this._value.text)),
                    });
            }

            var text = this._value.text;
            if (this.widget.obscureText) {
                text = new string(RenderEditable.obscuringCharacter, text.Length);
                int o = this._obscureShowCharTicksPending > 0 ? this._obscureLatestCharIndex : -1;
                if (o >= 0 && o < text.Length) {
                    text = text.Substring(0, o) + this._value.text.Substring(o, 1) + text.Substring(o + 1);
                }
            }

            return new TextSpan(style: this.widget.style, text: text);
        }
    }


    class _Editable : LeafRenderObjectWidget {
        public readonly TextSpan textSpan;
        public readonly TextEditingValue value;
        public readonly Color cursorColor;
        public readonly ValueNotifier<bool> showCursor;
        public readonly bool hasFocus;
        public readonly int maxLines;
        public readonly Color selectionColor;
        public readonly double textScaleFactor;
        public readonly TextAlign textAlign;
        public readonly TextDirection? textDirection;
        public readonly bool obscureText;
        public readonly bool autocorrect;
        public readonly ViewportOffset offset;
        public readonly SelectionChangedHandler onSelectionChanged;
        public readonly CaretChangedHandler onCaretChanged;
        public readonly bool rendererIgnoresPointer;


        public _Editable(TextSpan textSpan = null, TextEditingValue value = null,
            Color cursorColor = null, ValueNotifier<bool> showCursor = null, bool hasFocus = false,
            int maxLines = 0, Color selectionColor = null, double textScaleFactor = 1.0,
            TextDirection? textDirection = null, bool obscureText = false, TextAlign textAlign = TextAlign.left,
            bool autocorrect = false, ViewportOffset offset = null, SelectionChangedHandler onSelectionChanged = null,
            CaretChangedHandler onCaretChanged = null, bool rendererIgnoresPointer = false,
            Key key = null) : base(key) {
            this.textSpan = textSpan;
            this.value = value;
            this.cursorColor = cursorColor;
            this.showCursor = showCursor;
            this.hasFocus = hasFocus;
            this.maxLines = maxLines;
            this.selectionColor = selectionColor;
            this.textScaleFactor = textScaleFactor;
            this.textAlign = textAlign;
            this.textDirection = textDirection;
            this.obscureText = obscureText;
            this.autocorrect = autocorrect;
            this.offset = offset;
            this.onSelectionChanged = onSelectionChanged;
            this.onCaretChanged = onCaretChanged;
            this.rendererIgnoresPointer = rendererIgnoresPointer;
        }

        public override RenderObject createRenderObject(BuildContext context) {
            return new RenderEditable(
                text: this.textSpan,
                textDirection: this.textDirection ?? TextDirection.ltr,
                offset: this.offset,
                showCursor: this.showCursor,
                cursorColor: this.cursorColor,
                hasFocus: this.hasFocus,
                maxLines: this.maxLines,
                selectionColor: this.selectionColor,
                textScaleFactor: this.textScaleFactor,
                textAlign: this.textAlign,
                selection: this.value.selection,
                obscureText: this.obscureText,
                onSelectionChanged: this.onSelectionChanged,
                onCaretChanged: this.onCaretChanged,
                ignorePointer: this.rendererIgnoresPointer
            );
        }

        public override void updateRenderObject(BuildContext context, RenderObject renderObject) {
            var edit = (RenderEditable) renderObject;
            edit.text = this.textSpan;
            edit.cursorColor = this.cursorColor;
            edit.showCursor = this.showCursor;
            edit.hasFocus = this.hasFocus;
            edit.maxLines = this.maxLines;
            edit.selectionColor = this.selectionColor;
            edit.textScaleFactor = this.textScaleFactor;
            edit.textAlign = this.textAlign;
            edit.textDirection = this.textDirection;
            edit.selection = this.value.selection;
            edit.offset = this.offset;
            edit.onSelectionChanged = this.onSelectionChanged;
            edit.onCaretChanged = this.onCaretChanged;
            edit.ignorePointer = this.rendererIgnoresPointer;
            edit.obscureText = this.obscureText;
        }
    }
}
