﻿using UniInject;
using UniRx;
using UnityEngine;
using UnityEngine.UIElements;

#pragma warning disable CS0649

public class NoteAreaVerticalRulerControl : INeedInjection, IInjectionFinishedListener
{
    [Inject]
    private SongMeta songMeta;

    [Inject]
    private NoteAreaControl noteAreaControl;

    [Inject]
    private SongEditorSceneControl songEditorSceneControl;

    [Inject]
    private Settings settings;

    [Inject(UxmlName = R.UxmlNames.horizontalGridLabelContainer)]
    private VisualElement horizontalGridLabelContainer;

    [Inject(UxmlName = R.UxmlNames.horizontalGridLineContainer)]
    private VisualElement horizontalGridLineContainer;

    [Inject(UxmlName = R.UxmlNames.horizontalGrid)]
    private VisualElement horizontalGrid;

    [Inject]
    private GameObject gameObject;

    private ViewportEvent lastViewportEvent;

    private readonly Label[] labels = new Label[NoteAreaControl.MaxMidiNoteInViewport + 1];

    public void OnInjectionFinished()
    {
        horizontalGrid.RegisterCallbackOneShot<GeometryChangedEvent>(evt =>
        {
            UpdateMidiNoteLabels();

            if (settings.SongEditorSettings.GridSizeInPx > 0)
            {
                UpdateMidiNoteLines();
            }
        });

        noteAreaControl.ViewportEventStream.Subscribe(OnViewportChanged);

        settings.ObserveEveryValueChanged(_ => settings.SongEditorSettings.GridSizeInPx)
            .Subscribe(_ => UpdateMidiNoteLines())
            .AddTo(gameObject);
    }

    private void OnViewportChanged(ViewportEvent viewportEvent)
    {
        if (viewportEvent == null)
        {
            return;
        }

        if (lastViewportEvent == null
            || lastViewportEvent.Y != viewportEvent.Y
            || lastViewportEvent.Height != viewportEvent.Height)
        {
            UpdateMidiNoteLabels();
            UpdateMidiNoteLines();
        }
        lastViewportEvent = viewportEvent;
    }

    private void UpdateMidiNoteLines()
    {
        horizontalGridLineContainer.Clear();

        int minMidiNote = noteAreaControl.MinMidiNoteInCurrentViewport;
        int maxMidiNote = noteAreaControl.MaxMidiNoteInCurrentViewport;
        for (int midiNote = minMidiNote; midiNote <= maxMidiNote; midiNote++)
        {
            // Notes are drawn on lines and between lines alternatingly.
            bool hasLine = (midiNote % 2 == 0);
            if (hasLine)
            {
                Color color = (MidiUtils.GetRelativePitch(midiNote) == 0)
                    ? NoteAreaHorizontalRulerControl.highlightLineColor
                    : NoteAreaHorizontalRulerControl.normalLineColor;
                DrawHorizontalGridLine(midiNote, color);
            }
        }
    }

    private void UpdateMidiNoteLabels()
    {
        int minMidiNote = noteAreaControl.MinMidiNoteInCurrentViewport;
        int maxMidiNote = noteAreaControl.MaxMidiNoteInCurrentViewport;

        for (int midiNote = NoteAreaControl.MinViewportY; midiNote < minMidiNote; midiNote++)
        {
            if (labels[midiNote] != null)
            {
                labels[midiNote].HideByDisplay();
            }
        }

        for (int midiNote = minMidiNote; midiNote <= maxMidiNote; midiNote++)
        {
            if (labels[midiNote] == null)
            {
                labels[midiNote] = CreateLabelForMidiNote(midiNote);
            }
            else
            {
                UpdateMidiNoteLabelPosition(labels[midiNote], midiNote);
                labels[midiNote].ShowByDisplay();
            }
        }

        for (int midiNote = maxMidiNote + 1; midiNote <= NoteAreaControl.MaxMidiNoteInViewport; midiNote++)
        {
            if (labels[midiNote] != null)
            {
                labels[midiNote].HideByDisplay();
            }
        }
    }

    private Label CreateLabelForMidiNote(int midiNote)
    {
        Label label = new();
        label.AddToClassList("noteAreaGridLabel");
        label.AddToClassList("horizontalGridLabel");
        label.AddToClassList("tinyFont");
        label.enableRichText = false;
        label.style.position = new StyleEnum<Position>(Position.Absolute);
        label.style.unityTextAlign = new StyleEnum<TextAnchor>(TextAnchor.MiddleCenter);
        label.style.left = 0;

        string midiNoteName = MidiUtils.GetAbsoluteName(midiNote);
        label.text = midiNoteName;

        UpdateMidiNoteLabelPosition(label, midiNote);

        horizontalGridLabelContainer.Add(label);

        return label;
    }

    private void UpdateMidiNoteLabelPosition(Label label, int midiNote)
    {
        float heightPercent = noteAreaControl.HeightForSingleNote;
        float yPercent = (float)noteAreaControl.GetVerticalPositionForMidiNote(midiNote) - heightPercent / 2;
        label.style.top = new StyleLength(new Length(yPercent * 100, LengthUnit.Percent));
        label.style.height = new StyleLength(new Length(heightPercent * 100, LengthUnit.Percent));
    }

    private void DrawHorizontalGridLine(int midiNote, Color color)
    {
        float lineHeight = settings.SongEditorSettings.GridSizeInPx;
        if (lineHeight <= 0)
        {
            return;
        }

        float yPercent = (float)noteAreaControl.GetVerticalPositionForMidiNote(midiNote);
        VisualElement line = new();
        line.AddToClassList("gridLine");
        line.AddToClassList("horizontalGridLine");
        line.style.backgroundColor = color;
        line.style.top = new StyleLength(new Length(yPercent * 100, LengthUnit.Percent));
        line.style.height = new StyleLength(new Length(lineHeight, LengthUnit.Pixel));
        horizontalGridLineContainer.Add(line);
    }
}
