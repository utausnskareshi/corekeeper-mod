#nullable enable
using System;
using System.Collections.Generic;
using System.Text;
using PugMod;
using UnityEngine;

namespace CustomPlayerSkin
{
    /// <summary>
    /// Writes out the appearance the game is drawing, so the tool outside can start a drawing from
    /// how a character already looks.
    ///
    /// Nothing in a save file holds a picture. It holds which parts and which colours were chosen,
    /// and the artwork behind those choices sits inside the game's asset bundles, tinted by a
    /// shader as it is drawn. The finished appearance therefore exists only on screen, and this is
    /// the only place with access to it.
    ///
    /// Each layer's texture is copied into an off-screen buffer and read back from there, because
    /// art that came out of an asset bundle cannot be read from the processor directly. Which
    /// texture to copy has to be worked out the way the shader works it out - see
    /// <see cref="DrawnTextureFor"/> - because the game's own customization loads the chosen art
    /// into the material's replacement slot rather than over the prefab's texture.
    ///
    /// Copied plainly, not drawn through the layer's material. Going through the material was the
    /// first attempt and it produced nine entirely empty bands: the game packs its sprites into
    /// one large atlas and draws them instanced, so its sprite materials expect per-instance data
    /// that a full-screen quad does not supply.
    ///
    /// The one thing the shader adds that matters is the colour, and it is reproduced here instead
    /// of rendered - see <see cref="ApplyColorReplacement"/>. A character is a shape and a palette
    /// held apart: the sheet that loads carries the shape in whatever colours the artist drew, and
    /// a lookup table turns those into this character's. Copying the texture alone therefore got
    /// the build and the hairstyle right and every colour wrong.
    ///
    /// The nine results are stacked into one image, a band per layer, rather than flattened,
    /// because what to include - equipment above all - is a choice that belongs to whoever is
    /// drawing, and flattening here would make changing that choice cost a restart of the game.
    /// </summary>
    internal static class SkinCapture
    {
        /// <summary>Directory the captures are written into.</summary>
        public const string CapturedDirectory = CustomPlayerSkinMod.RootDirectory + "captured/";

        /// <summary>
        /// Version of the band layout below. The tool refuses a number it does not recognise
        /// rather than compositing bands whose meaning it is guessing at.
        /// </summary>
        private const int Format = 1;

        /// <summary>
        /// The bands, in the order the game draws them: earlier ones end up underneath. The tool
        /// stacks them in this order, and both sides check the list against each other.
        /// </summary>
        private static readonly string[] LayerNames =
        {
            "body", "hair", "hairShade", "eyes", "shirt", "pants", "helm", "breastArmor", "pantsArmor",
        };

        /// <summary>
        /// The bands that are equipment, which the tool composites only when asked to.
        ///
        /// Named here as well as in the tool because the two sides treat them differently and the
        /// difference has to agree: equipment counts as present only while the game is drawing it,
        /// where a base layer counts as soon as its art is loaded. A contract test compares the
        /// two lists.
        /// </summary>
        private static readonly string[] EquipmentLayerNames = { "helm", "breastArmor", "pantsArmor" };

        /// <summary>
        /// How often a character may be captured while the picture is still changing, in seconds.
        ///
        /// The appearance is rebuilt on every equipment change and on every respawn, and reading
        /// pixels back from the graphics card stalls the frame it happens on. The interval is also
        /// what makes this recover from the layers not being loaded yet: the first attempt after a
        /// rebuild often finds them missing, and the next one a few seconds later does not.
        /// </summary>
        private const float IntervalSeconds = 5f;

        /// <summary>
        /// How often to look once the picture has stopped changing, in seconds.
        ///
        /// A character standing still gave the same answer every five seconds for as long as the
        /// game was open. The work is the same whether the answer is new or not - the whole sheet
        /// is built and read back before the comparison can be made - so this is a game paying,
        /// for hours, for something the player needed once.
        /// </summary>
        private const float SettledIntervalSeconds = 60f;

        /// <summary>How many identical captures in a row count as settled.</summary>
        private const int SettledAfter = 2;

        /// <summary>
        /// How long after an appearance rebuild to look, in seconds.
        ///
        /// A rebuild is the only thing that changes how a character looks, so it cancels whatever
        /// back-off had built up from finding the same picture over and over. Keeping that back-off
        /// meant an equipment change went unnoticed for up to a minute - easily long enough to
        /// equip something, press the button in the tool, and be handed the character as they were
        /// before.
        ///
        /// Not zero, because none of the new art has arrived at the moment of the rebuild: it is
        /// requested there and lands on a callback. A short wait lets it turn up, and collapses the
        /// burst of rebuilds that equipping one item actually produces into a single attempt.
        /// </summary>
        private const float RebuildGraceSeconds = 1.5f;

        /// <summary>
        /// The switch that decides which of the two textures the shader samples.
        ///
        /// Read, not written. <see cref="SpriteSheetSkin.SetSkin"/> is the game's own method and
        /// its own customization goes through it, so the art a character is actually wearing is
        /// usually the replacement rather than the prefab's texture. Forcing the switch off - the
        /// first attempt at this - asks for the default art instead of the chosen art.
        ///
        /// It also means a character this tool has already replaced captures as it now looks
        /// rather than as it looked before, which is what the button claims to fetch.
        /// </summary>
        private const string UseReplacementProperty = "_UseReplacementTex";

        /// <summary>The texture the switch above selects when it is on.</summary>
        private const string ReplacementProperty = "_ReplacementTex";

        /// <summary>
        /// The colour translation table the shader applies on top of the texture.
        ///
        /// A character is not one picture but a shape and a set of colours, held separately: the
        /// save keeps <c>body</c> beside <c>skinColor</c>, <c>hair</c> beside <c>hairColor</c>, and
        /// so on. The sheet that gets loaded carries only the shape, drawn in whatever palette the
        /// artist used, and a <c>ColorReplacer</c> on the same object hands the shader a two-row
        /// lookup - source colours along the top, their replacements underneath - which is what
        /// turns that palette into this character's.
        ///
        /// Copying the texture alone therefore gets the shape right and every colour wrong, which
        /// is exactly what it did: the right build and hairstyle in the artist's green shirt and
        /// brown hair. The translation is a plain per-pixel mapping, so this reproduces it here
        /// rather than trying to render through the game's own material, which cannot work - see
        /// the note on this class.
        /// </summary>
        private const string ColorReplaceProperty = "_colorReplaceTexture";

        /// <summary>
        /// How often to look for a character still waiting to be captured, in seconds.
        ///
        /// A rebuild of the appearance is only where the game *asks* Addressables for the
        /// artwork - <c>LoadAndSetSkinAsync</c> starts a load and returns. The art lands later, on
        /// <c>OnLoadCompleted</c>, which is what fills in <c>SpriteSheetSkin.skin</c> and pushes it
        /// onto the material. Capturing solely from the rebuild therefore sampled the one moment
        /// the character's own art is certain to be absent, and nothing ever came back to look
        /// again: what got written out was the prefab's default character, permanently.
        /// </summary>
        private const float PollSeconds = 1f;

        /// <summary>When the poll below may next do any work.</summary>
        private static float _nextPoll;

        /// <summary>When each character may next be captured.</summary>
        private static readonly Dictionary<string, float> NextAttempt = new();

        /// <summary>
        /// The character seen on the last attempt.
        ///
        /// Kept so the poll can tell that nothing is due yet without searching the scene for the
        /// player first. Once a capture has settled that search is the only cost left, and this
        /// takes it down to once a minute.
        /// </summary>
        private static string? _lastGuid;

        /// <summary>
        /// What was last written for each character, as a hash of the pixels.
        ///
        /// Compared before writing, so a character standing still does not have the same image
        /// written over and over: the interval decides how often it is looked at, this decides
        /// whether looking led to anything.
        /// </summary>
        private static readonly Dictionary<string, int> LastWritten = new();

        /// <summary>Whether the manifest has been written this session.</summary>
        private static bool _manifestWritten;

        /// <summary>Reasons already logged, so one that stays true does not fill the log.</summary>
        private static readonly HashSet<string> Complained = new();

        /// <summary>Upper bound on distinct reasons kept, guarding against unforeseen variety.</summary>
        private const int MaxComplaints = 32;

        /// <summary>Whether the layer-by-layer description has been logged this session.</summary>
        private static bool _describedLayers;

        /// <summary>Whether what each band ended up holding has been logged this session.</summary>
        private static bool _countedBands;

        /// <summary>
        /// Where each band's picture came from, for the log.
        ///
        /// Which of the four candidates a band ends up using is the whole question when a capture
        /// comes out looking like somebody else: the prefab's own texture is the game's default
        /// character, so a band that used it is a band that missed the character's real art.
        /// </summary>
        private static readonly string?[] SourceUsed = new string?[LayerNames.Length];

        /// <summary>How many times in a row a character has captured to the same picture.</summary>
        private static readonly Dictionary<string, int> Unchanged = new();

        /// <summary>
        /// The buffers a capture works in, held between captures rather than allocated each time.
        ///
        /// Every one of them is the same size on every capture, and a capture happens for as long
        /// as the game is open. Allocating them afresh each time left about two and a half
        /// megabytes of garbage behind on every attempt, on the game's own heap.
        /// </summary>
        private static Color32[]? _stacked;
        private static Texture2D? _readback;
        private static Texture2D? _encode;

        /// <summary>
        /// Captures the appearance of one player, if it is time to and if it can be.
        ///
        /// Reached two ways: from the appearance rebuild, where it runs ahead of the applier, and
        /// from <see cref="Poll"/>, where nothing follows it. Nothing here throws: a failure to
        /// capture costs a feature of the tool outside, and must not cost the game its frame.
        /// </summary>
        /// <param name="rebuilt">
        /// True when the game has just rebuilt this character's appearance, which is the only
        /// occasion it can have changed. Such a call drops the settled back-off; see
        /// <see cref="RebuildGraceSeconds"/>.
        /// </param>
        public static void Capture(PlayerController player, bool rebuilt = false)
        {
            try
            {
                // Not on the way out. Shutdown asks the game to rebuild every player back to its
                // own appearance, and that rebuild fires the very patch this sits in - so without
                // this the last capture of every session was the game's default character, taken
                // at the one moment the layers hold no replacement, and taken while the world is
                // being torn down. It also kept nine readbacks, an encode and two file writes on
                // the quit path, some of it into buffers Reset destroys moments later.
                if (CustomPlayerSkinMod.IsShuttingDown)
                {
                    return;
                }

                // Only the character being played on this machine. The tool lists the characters
                // saved here, so another player's character is not one it could ever offer.
                //
                // Ahead of the setting on purpose. Reading a setting opens and parses its own
                // file - the reload timer says so where it makes the same trade - and this runs
                // for every player on screen on every appearance rebuild, so asking the cheap
                // question first is what keeps a full server from paying for it eight times over.
                if (!player.isLocal)
                {
                    return;
                }

                if (!CustomPlayerSkinMod.Config.CaptureAppearance.Value)
                {
                    return;
                }

                string? guid = CharacterIdentity.For(player);
                if (guid == null)
                {
                    return;
                }

                _lastGuid = guid;

                float now = Time.unscaledTime;

                // Not while this mod is the one drawing the character. The layers then hold this
                // mod's own picture, and a transparent texture wherever the settings hide a layer,
                // so a capture taken now writes that over the file the fetch button reads - losing
                // the game's own appearance and handing back a character with no hair or clothes.
                //
                // Backed off rather than simply skipped, so the poll below stops searching the
                // scene every second for a character it is not going to capture. Taking the
                // picture off again clears the entry this asks about and rebuilds the appearance,
                // so captures start again by themselves within a couple of seconds.
                //
                // A character who already had a picture applied when the game started therefore
                // never gets captured, and that is the honest answer: their own appearance is not
                // what is on screen. Fetching it means taking the picture off first.
                if (CustomPlayerSkinMod.IsReplacing(guid))
                {
                    NextAttempt[guid] = now + SettledIntervalSeconds;
                    return;
                }

                if (rebuilt)
                {
                    // Whatever back-off had accumulated no longer applies: the picture has just
                    // been rebuilt, so the reason for looking less often has gone.
                    Unchanged.Remove(guid);

                    // Three cases, and the middle one is the only one that leaves the time alone.
                    //
                    // A deadline further out than the grace period is pulled in, so an equipment
                    // change is noticed at once. A deadline already inside the grace period is
                    // left where it is, which is what collapses the burst of rebuilds that
                    // equipping one item produces into a single attempt.
                    //
                    // A deadline that has already passed has to be pushed out, and this is what
                    // was missing: it was left in the past, so the gate below let the capture run
                    // right here, inside the rebuild - the one moment the layers are certain to
                    // be stale, because the new art has only just been asked for and arrives on a
                    // callback. For a character this mod draws over it was worse than stale. The
                    // entry IsReplacing asks about is dropped five minutes after the last rebuild
                    // even while the character is on screen, and dropping it is itself what
                    // triggers the rebuild that lands here - so IsReplacing was momentarily false
                    // while the layers still held this mod's own picture and a transparent
                    // texture in every hidden layer. What got written was the user's own applied
                    // drawing as the body with every other band empty, and because the applier
                    // re-creates the entry immediately afterwards, nothing ever corrected it. The
                    // file then exists, so the fetch button reports success and hands that back.
                    bool known = NextAttempt.TryGetValue(guid, out float due);
                    if (!known || due <= now || due > now + RebuildGraceSeconds)
                    {
                        NextAttempt[guid] = now + RebuildGraceSeconds;
                    }
                }

                if (NextAttempt.TryGetValue(guid, out float next) && now < next)
                {
                    return;
                }

                NextAttempt[guid] = now + IntervalSeconds;

                CaptureNow(player, guid);
            }
            catch (Exception ex)
            {
                Complain($"見た目の書き出しに失敗した: {ex}");
            }
        }

        /// <summary>
        /// Gives a capture that is still waiting another chance, from the mod's own update.
        ///
        /// Called every frame; almost every call returns on the first line. The work a capture
        /// does is gated by <see cref="IntervalSeconds"/> as before - this only makes sure the
        /// gate is reached at all. Without it the only thing that ever asked for a capture was an
        /// appearance rebuild, which happens before the character's artwork has finished loading,
        /// so the wait for that artwork could never end.
        /// </summary>
        public static void Poll()
        {
            try
            {
                float now = Time.unscaledTime;
                if (now < _nextPoll)
                {
                    return;
                }

                _nextPoll = now + PollSeconds;

                if (CustomPlayerSkinMod.IsShuttingDown)
                {
                    return;
                }

                // Nothing due for the character last seen means nothing to do, and saying so here
                // is what keeps a scene-wide search out of the frame budget: after a capture has
                // settled the interval is a minute, so that is how often the search below runs.
                if (_lastGuid != null
                    && NextAttempt.TryGetValue(_lastGuid, out float due)
                    && now < due)
                {
                    return;
                }

                if (!CustomPlayerSkinMod.Config.CaptureAppearance.Value)
                {
                    return;
                }

                // Only the character being played here can be captured, so the search stops at
                // the first one it finds. Inactive objects are skipped for the same reason the
                // rebuild skips them: the game pools these, and a pooled one is not on screen.
                foreach (PlayerController player in UnityEngine.Object.FindObjectsByType<PlayerController>(
                             FindObjectsInactive.Exclude, FindObjectsSortMode.None))
                {
                    if (player != null && player.isLocal)
                    {
                        Capture(player);
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                Complain($"見た目の取り込みを試せなかった: {ex}");
            }
        }

        /// <summary>Forgets everything remembered, so a fresh session starts clean.</summary>
        public static void Reset()
        {
            NextAttempt.Clear();
            LastWritten.Clear();
            Unchanged.Clear();
            Complained.Clear();
            _manifestWritten = false;
            _describedLayers = false;
            _countedBands = false;
            _nextPoll = 0f;
            _lastGuid = null;
            Array.Clear(SourceUsed, 0, SourceUsed.Length);

            // The held buffers go too. A reload that kept them would be holding textures created
            // by the session that has just ended.
            Release(ref _readback);
            Release(ref _encode);
            _stacked = null;
        }

        /// <summary>Destroys a held texture and forgets it.</summary>
        private static void Release(ref Texture2D? texture)
        {
            if (texture != null)
            {
                UnityEngine.Object.Destroy(texture);
            }

            texture = null;
        }

        /// <summary>
        /// A texture of the given size to read pixels back into, made once and reused.
        /// Remade if the size ever differs, which it does not today.
        /// </summary>
        private static Texture2D Readback(int width, int height)
        {
            if (_readback != null && (_readback.width != width || _readback.height != height))
            {
                Release(ref _readback);
            }

            return _readback ??= new Texture2D(width, height, TextureFormat.RGBA32, mipChain: false);
        }

        /// <summary>The texture the finished stack is encoded from, made once and reused.</summary>
        private static Texture2D Encode(int width, int height)
        {
            if (_encode != null && (_encode.width != width || _encode.height != height))
            {
                Release(ref _encode);
            }

            return _encode ??= new Texture2D(width, height, TextureFormat.RGBA32, mipChain: false);
        }

        private static void CaptureNow(PlayerController player, string guid)
        {
            SpriteSheetSkin?[] layers =
            {
                player.bodySkin, player.hairSkin, player.hairShadeSkin, player.eyesSkin,
                player.shirtSkin, player.pantsSkin, player.helmSkin, player.breastArmorSkin,
                player.pantsArmorSkin,
            };

            if (layers.Length != LayerNames.Length)
            {
                Complain($"レイヤーの数が合わない: {layers.Length} と {LayerNames.Length}");
                return;
            }

            // Waited for, because until it arrives the layers are still showing somebody else.
            //
            // The game asks Addressables for a character's artwork and carries on: the request is
            // made in RefreshCustomization, and the answer lands later on OnLoadCompleted, which
            // is what assigns SpriteSheetSkin.skin and then calls ApplySkin to put it on the
            // material. Between the two, the material's replacement slot is empty, the shader's
            // switch is off, and what the shader samples - and therefore what a faithful capture
            // reads - is the prefab's own texture, which is the game's default character.
            //
            // This ran in the rebuild's postfix, which is exactly that gap, so every capture
            // wrote out the default: light skin, brown hair, green shirt, whoever the character
            // actually was. Nothing looked wrong from the inside, either. The default texture is
            // a valid sheet of the right size, so the attempt succeeded, the picture was written,
            // and the settled counter then stopped it being looked at again.
            //
            // `skin` is the field the load fills in, so it is precisely the signal for "this is
            // the character now" rather than "this is what was here before".
            SpriteSheetSkin? bodyLayer = layers[0];
            if (bodyLayer == null || bodyLayer.skin == null)
            {
                // Described once, so a wait that never ends can be told from one that has not
                // finished yet. Poll brings us back here; the interval decides how often.
                DescribeLayers(layers);
                return;
            }

            int width = SkinApplier.ExpectedWidth;
            int height = SkinApplier.ExpectedHeight;

            // One buffer for the whole stack. Each band is read into its own slice of it, so the
            // encode at the end sees a single image rather than nine to join up. Held between
            // captures: it is the same size every time, and a capture happens for as long as the
            // game is open. Cleared rather than trusted, because a layer that is absent this time
            // leaves its slice untouched and would otherwise show the previous capture's art.
            int size = width * height * LayerNames.Length;
            if (_stacked == null || _stacked.Length != size)
            {
                _stacked = new Color32[size];
            }

            Color32[] stacked = _stacked;
            Array.Clear(stacked, 0, stacked.Length);

            // Set when a layer that is being drawn is still waiting for its art. The picture is
            // written anyway - the rest of it is worth having - but it is not allowed to settle,
            // so the attempt a few seconds later replaces it with the finished one.
            bool incomplete = false;

            for (int band = 0; band < layers.Length; band++)
            {
                // Only the body has to be there. Everything above it is optional - no hat, no
                // armour, sometimes no hair - and an empty band is the honest picture of that.
                // The body missing means the layers have not arrived yet, and that is worth
                // waiting for rather than writing an empty character out.
                bool required = band == 0;

                if (!TryReadLayer(
                        layers[band], LayerNames[band], required, width, height, stacked, band,
                        ref incomplete))
                {
                    // Left for the next interval: a layer still loading is the ordinary case right
                    // after the appearance is rebuilt. Described once all the same, because a run
                    // that keeps giving up in silence is a run that cannot be diagnosed.
                    DescribeLayers(layers);
                    return;
                }
            }

            int hash = HashOf(stacked);
            if (LastWritten.TryGetValue(guid, out int previous) && previous == hash)
            {
                if (incomplete)
                {
                    // Matching the last picture written is not the same as being finished when a
                    // layer is still on its way: settling here is how a character came to be
                    // remembered without the armour they were wearing.
                    Unchanged[guid] = 0;
                    return;
                }

                // Nothing to write. Once it has answered the same way a couple of times, look
                // less often: the picture only changes when the player changes, and the next
                // change resets this to nothing.
                Unchanged[guid] = Unchanged.TryGetValue(guid, out int same) ? same + 1 : 1;

                if (Unchanged[guid] >= SettledAfter)
                {
                    NextAttempt[guid] = Time.unscaledTime + SettledIntervalSeconds;
                }

                return;
            }

            // Something is different, so the picture is still moving. Back to looking often.
            Unchanged[guid] = 0;

            Texture2D sheet = Encode(width, height * LayerNames.Length);

            // Braced, not a try: the texture is held for the next capture rather than destroyed,
            // so there is nothing left to undo on the way out.
            {
                sheet.SetPixels32(stacked);
                sheet.Apply(updateMipmaps: false, makeNoLongerReadable: false);

                byte[] png = ImageConversion.EncodeToPNG(sheet);
                if (png == null || png.Length == 0)
                {
                    Complain("見た目を PNG に変換できなかった。");
                    return;
                }

                SkinStore.EnsureDirectory(CustomPlayerSkinMod.RootDirectory);
                SkinStore.EnsureDirectory(CapturedDirectory);

                // Written first, and only once. The tool looks for the image before it looks at
                // the manifest, so a manifest with no image beside it reads as "nothing captured
                // yet" - which is true - while an image with no manifest reads as a mod that needs
                // updating, which is not. Of the two half-finished states, this is the honest one.
                if (!WriteManifest())
                {
                    return;
                }

                if (!SkinStore.Write(CapturedDirectory + guid + ".png", png))
                {
                    return;
                }

                LastWritten[guid] = hash;
                CustomPlayerSkinMod.Log($"見た目を書き出した: {guid}");
                LogBandContents(stacked, width, height);
            }
        }

        /// <summary>
        /// Copies one layer's texture into its band, with the colour translation applied.
        /// </summary>
        /// <param name="required">
        /// Whether the layer has to be there. A layer that is absent and not required leaves its
        /// band transparent, which is what wearing no helmet looks like; one that is absent and
        /// required means the appearance has not finished loading, so the whole capture waits.
        /// </param>
        /// <returns>False only when the capture should be abandoned for now.</returns>
        /// <param name="incomplete">
        /// Set when a layer that is being drawn has not received its art yet, so the caller knows
        /// this picture is not the finished one and must not let it settle.
        /// </param>
        private static bool TryReadLayer(
            SpriteSheetSkin? layer, string name, bool required,
            int width, int height, Color32[] into, int band, ref bool incomplete)
        {
            SpriteRenderer? renderer = layer == null ? null : layer.sr;
            Material? material = renderer == null ? null : renderer.sharedMaterial;

            bool equipment = IsEquipmentLayer(name);
            bool drawn = renderer != null && renderer.enabled && renderer.gameObject.activeInHierarchy;
            bool loaded = layer != null && layer.skin != null;

            // Whether the layer counts as present depends on which kind it is, because the game
            // switches renderers off for two quite different reasons.
            //
            // Equipment off means the character is not wearing any, and the material still holds
            // the prefab's own art - so reading it put a full set of default armour into exactly
            // the bands the "include equipment" tick composites. Equipment therefore counts only
            // while it is actually being drawn.
            //
            // A base layer off means something is covering it: armour hides the shirt, a full helm
            // hides the hair. The game loads that art all the same, and a picture of the character
            // without their equipment - which the tool offers - is precisely the shirt underneath.
            // So a base layer counts as soon as its art has loaded, on screen or not.
            bool present = equipment ? drawn && loaded : loaded;

            // Drawn but not loaded means the art is still on its way. The band is left empty rather
            // than taken from the prefab, and the capture is marked unfinished so that it cannot
            // settle: the attempt a few seconds later finds the real art and replaces it.
            bool loading = drawn && !loaded;
            if (loading)
            {
                incomplete = true;
            }

            string used = "absent";
            Texture2D? source = layer == null || material == null || !present
                ? null
                : DrawnTextureFor(layer, renderer, material, out used);

            if (loading)
            {
                used = "読み込み中";
            }
            else if (!present && renderer != null)
            {
                used = equipment ? "未装備" : "絵が無い";
            }

            if (band >= 0 && band < SourceUsed.Length)
            {
                SourceUsed[band] = used;
            }

            if (source == null || material == null)
            {
                // Nothing to draw. Left empty unless this is the layer whose absence means the
                // art is still on its way from Addressables.
                return !required;
            }

            if (source.width != width || source.height != height)
            {
                Complain($"レイヤーの寸法が想定と違う（{name}: {source.width}x{source.height}）。");

                // The band is left empty rather than the capture abandoned: a layer of the wrong
                // shape would never come right, and refusing over it would mean never writing
                // anything at all for this character.
                return !required;
            }

            // Read first, and it cannot throw, so there is nothing to undo if what follows does.
            RenderTexture? previous = RenderTexture.active;
            RenderTexture? buffer = null;

            try
            {
                // sRGB, to match the art. If the captured colours come out lighter or darker than
                // what is on screen while the shapes are right, this is the line to look at: the
                // game's colour space decides whether the buffer should convert or pass through.
                buffer = RenderTexture.GetTemporary(
                    width, height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);

                Texture2D read = Readback(width, height);

                // Cleared before the copy. A temporary buffer starts with whatever was last in it,
                // and while the copy below overwrites every pixel, clearing costs nothing and
                // means a copy that somehow does not cover the buffer leaves transparency rather
                // than somebody else's picture.
                RenderTexture.active = buffer;
                GL.Clear(clearDepth: true, clearColor: true, new Color(0f, 0f, 0f, 0f));

                // A plain copy: no material, so no shader of the game's is involved. See the note
                // on this class for why drawing through the layer's own material cannot work here.
                Graphics.Blit(source, buffer);

                // Set again: Blit leaves its own target selected, not necessarily this one.
                RenderTexture.active = buffer;
                read.ReadPixels(new Rect(0, 0, width, height), 0, 0, recalculateMipMaps: false);
                read.Apply(updateMipmaps: false, makeNoLongerReadable: false);

                Color32[] pixels = read.GetPixels32();

                // The colours this character wears, put on here because the plain copy above
                // cannot bring them: they are the shader's work, not the texture's.
                int replaced = ApplyColorReplacement(pixels, material, band);
                if (replaced > 0 && band >= 0 && band < SourceUsed.Length)
                {
                    SourceUsed[band] = used + " 色置換" + replaced;
                }

                // Copied row for row, but into the slice counted from the other end. Two
                // conventions meet here and they cancel out to exactly this:
                //
                //   - a Texture2D holds row 0 at the bottom, and both GetPixels32 and
                //     SetPixels32 follow that;
                //   - EncodeToPNG writes the picture the right way up, so texture row r becomes
                //     PNG row (totalHeight - 1 - r).
                //
                // The tool wants band 0 at the top of the PNG. Working the two together, band b
                // has to occupy texture rows starting at (bands - 1 - b) * height, and within
                // that slice the rows keep the order ReadPixels gave them. Flipping the rows as
                // well - the obvious-looking fix - turns every layer upside down and reverses the
                // order of the bands at the same time, which very nearly looks deliberate.
                int slice = (LayerNames.Length - 1 - band) * width * height;
                Array.Copy(pixels, 0, into, slice, pixels.Length);

                return true;
            }
            finally
            {
                RenderTexture.active = previous;

                if (buffer != null)
                {
                    RenderTexture.ReleaseTemporary(buffer);
                }
            }
        }

        /// <summary>
        /// Reused by the colour translation below, so a capture leaves no garbage behind.
        /// Small - a layer has a handful of colours - and cleared before each use.
        /// </summary>
        private static readonly Dictionary<int, Color32> ReplaceMap = new();

        /// <summary>
        /// Translates one band's colours the way the shader would.
        ///
        /// The lookup the game builds is a two-row texture: each source colour along the top row
        /// with the colour it becomes directly beneath it. Both rows come from the same lists the
        /// character's chosen palette filled in, so reading the texture gives the mapping already
        /// rounded to the byte values the art is stored in.
        ///
        /// Matched on the three colour channels only. The pixel keeps its own alpha, so a
        /// half-transparent edge stays half-transparent instead of taking the palette's opacity.
        /// A colour with no entry is left exactly as it was, which is what the shader does too.
        /// </summary>
        /// <returns>How many pixels were translated. Zero means this layer has no palette.</returns>
        private static int ApplyColorReplacement(Color32[] pixels, Material material, int band)
        {
            Texture2D? lookup = material.HasProperty(ColorReplaceProperty)
                ? material.GetTexture(ColorReplaceProperty) as Texture2D
                : null;

            // Fewer than two rows means the table holds sources with nothing to map them to
            if (lookup == null || lookup.width <= 0 || lookup.height < 2)
            {
                // Only worth remarking on for the body. The equipment layers carry their colours
                // in the artwork and have no table at all, so saying so for them is noise - but
                // a body with no table means this is looking in the wrong place, and that is the
                // difference between a capture of this character and a capture of the artist's.
                if (band == 0)
                {
                    Complain(
                        $"肌の色の対応表が見つからない（{ColorReplaceProperty}）。" +
                        "取り込んだ絵の色がゲームと違う場合はここが原因。");
                }

                return 0;
            }

            Color32[] table;
            try
            {
                table = lookup.GetPixels32();
            }
            catch (Exception ex)
            {
                // Said once. A table that cannot be read on the processor would fail every time,
                // and the band is still worth writing - in the artist's palette rather than none.
                Complain($"色の対応表を読み取れなかった: {ex.Message}");
                return 0;
            }

            int colours = lookup.width;
            if (table.Length < colours * 2)
            {
                return 0;
            }

            // Which row holds the colours to translate from is settled by looking rather than
            // assumed. The table is two rows and the order they are written in is the game's
            // business, not something this can read off; getting it backwards would map the
            // character's own colours to the artist's palette, which is a wrong answer that looks
            // just as deliberate as the right one. The source row is by definition the one whose
            // colours appear in this texture, so counting both ways round settles it.
            Prepare(table, fromRow: 0, toRow: colours, colours);
            int forwardHits = CountHits(pixels);

            Prepare(table, fromRow: colours, toRow: 0, colours);
            int backwardHits = CountHits(pixels);

            if (forwardHits >= backwardHits)
            {
                Prepare(table, fromRow: 0, toRow: colours, colours);
            }

            if (ReplaceMap.Count == 0 || (forwardHits == 0 && backwardHits == 0))
            {
                return 0;
            }

            int replaced = 0;
            for (int i = 0; i < pixels.Length; i++)
            {
                Color32 pixel = pixels[i];
                if (pixel.a == 0)
                {
                    continue;
                }

                if (!ReplaceMap.TryGetValue(Key(pixel), out Color32 to))
                {
                    continue;
                }

                pixels[i] = new Color32(to.r, to.g, to.b, pixel.a);
                replaced++;
            }

            return replaced;
        }

        /// <summary>Loads the translation table for one direction into the reused map.</summary>
        private static void Prepare(Color32[] table, int fromRow, int toRow, int colours)
        {
            ReplaceMap.Clear();

            for (int c = 0; c < colours; c++)
            {
                Color32 from = table[fromRow + c];

                // An unused column: the table is as wide as the longer of the two lists, so the
                // tail of the shorter one is left transparent rather than filled in.
                if (from.a == 0)
                {
                    continue;
                }

                ReplaceMap[Key(from)] = table[toRow + c];
            }
        }

        /// <summary>How many of a band's pixels the loaded table would translate.</summary>
        private static int CountHits(Color32[] pixels)
        {
            int hits = 0;

            for (int i = 0; i < pixels.Length; i++)
            {
                Color32 pixel = pixels[i];
                if (pixel.a != 0 && ReplaceMap.ContainsKey(Key(pixel)))
                {
                    hits++;
                }
            }

            return hits;
        }

        /// <summary>The three colour channels packed into one value, for looking a colour up.</summary>
        private static int Key(Color32 colour) => (colour.r << 16) | (colour.g << 8) | colour.b;

        /// <summary>Whether a band is equipment rather than part of the character underneath.</summary>
        private static bool IsEquipmentLayer(string name)
        {
            for (int i = 0; i < EquipmentLayerNames.Length; i++)
            {
                if (string.Equals(EquipmentLayerNames[i], name, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// The texture a layer is drawing, chosen the way the shader chooses it.
        ///
        /// The replacement comes first when the switch says the shader is sampling it, because
        /// that is where the game's own customization puts the art it loaded. The prefab's texture
        /// is next, then the two places a layer might still be remembering it. The first attempt
        /// at this read only <see cref="SpriteSheetSkin.skin"/>, which in practice is null, so
        /// every capture gave up on the body layer before it had drawn anything.
        /// </summary>
        private static Texture2D? DrawnTextureFor(
            SpriteSheetSkin layer, SpriteRenderer? renderer, Material material, out string source)
        {
            bool usesReplacement =
                material.HasProperty(UseReplacementProperty)
                && material.GetFloat(UseReplacementProperty) > 0.5f;

            if (usesReplacement
                && material.HasProperty(ReplacementProperty)
                && material.GetTexture(ReplacementProperty) is Texture2D replacement)
            {
                source = "replacement";
                return replacement;
            }

            if (material.mainTexture is Texture2D main)
            {
                // The prefab's own texture: the game's default character. Right only for a layer
                // the game genuinely leaves alone, which is why it stays - but a body captured
                // from here is the wrong character, so it is worth being able to see in the log.
                source = "prefab";
                return main;
            }

            if (layer.skin != null)
            {
                source = "skin";
                return layer.skin;
            }

            Sprite? sprite = renderer == null ? null : renderer.sprite;
            source = sprite == null ? "none" : "sprite";
            return sprite == null ? null : sprite.texture;
        }

        /// <summary>Says what the bands are, for the tool to check before it reads one.</summary>
        /// <returns>True when the manifest is on disk, whether this call is what put it there.</returns>
        private static bool WriteManifest()
        {
            if (_manifestWritten)
            {
                return true;
            }

            StringBuilder text = new StringBuilder();
            text.Append("{ \"format\": ").Append(Format).Append(", \"layers\": [");
            for (int i = 0; i < LayerNames.Length; i++)
            {
                if (i > 0)
                {
                    text.Append(", ");
                }

                text.Append('"').Append(LayerNames[i]).Append('"');
            }

            text.Append("] }");

            _manifestWritten = SkinStore.Write(
                CapturedDirectory + "manifest.json", Encoding.UTF8.GetBytes(text.ToString()));

            return _manifestWritten;
        }

        /// <summary>
        /// Counts what ended up in each band, once per session.
        ///
        /// A capture that writes a file of the right size and shape, full of nothing, looks like a
        /// success from every side except the picture - which is exactly what the first working
        /// version of this did. The counts turn one line of the game's log into the answer.
        /// </summary>
        private static void LogBandContents(Color32[] stacked, int width, int height)
        {
            if (_countedBands)
            {
                return;
            }

            _countedBands = true;

            for (int band = 0; band < LayerNames.Length; band++)
            {
                // The bands sit in the buffer counted from the other end, the same way they are
                // written; see the note where they are copied in.
                int slice = (LayerNames.Length - 1 - band) * width * height;
                int painted = 0;

                for (int i = 0; i < width * height; i++)
                {
                    if (stacked[slice + i].a != 0)
                    {
                        painted++;
                    }
                }

                // The source is logged beside the count because the two answer different halves of
                // the same question. A band full of pixels taken from "prefab" is the game's
                // default character rather than this one, and that reads as a success everywhere
                // else: the file is written, the size is right, and the picture is of a stranger.
                string? recorded = band < SourceUsed.Length ? SourceUsed[band] : null;

                CustomPlayerSkinMod.Log(
                    $"  {LayerNames[band]}: 不透明な画素 {painted}（取得元 {recorded ?? "?"}）");
            }
        }

        /// <summary>A cheap hash of the pixels, only ever compared with another of its own kind.</summary>
        private static int HashOf(Color32[] pixels)
        {
            unchecked
            {
                int hash = 17;
                foreach (Color32 pixel in pixels)
                {
                    hash = (hash * 31) + pixel.r;
                    hash = (hash * 31) + pixel.g;
                    hash = (hash * 31) + pixel.b;
                    hash = (hash * 31) + pixel.a;
                }

                return hash;
            }
        }

        /// <summary>
        /// Logs a reason once. This runs on a timer for as long as the game is open, so a reason
        /// that keeps being true would otherwise fill the log with the same line.
        /// </summary>
        private static void Complain(string message)
        {
            if (Complained.Count >= MaxComplaints || !Complained.Add(message))
            {
                return;
            }

            CustomPlayerSkinMod.LogWarning(message);
        }

        /// <summary>
        /// Writes down what each layer is holding, once per session.
        ///
        /// Logged when a capture cannot go ahead. Where the art of a layer lives is not something
        /// this mod can decide from outside - it is the prefab's texture for some layers and the
        /// replacement for others - and a run that gives up in silence teaches nothing. This turns
        /// one run of the game into an answer.
        /// </summary>
        private static void DescribeLayers(SpriteSheetSkin?[] layers)
        {
            if (_describedLayers)
            {
                return;
            }

            _describedLayers = true;
            CustomPlayerSkinMod.LogWarning("見た目を書き出せなかった。各レイヤーの状態:");

            for (int band = 0; band < layers.Length && band < LayerNames.Length; band++)
            {
                SpriteSheetSkin? layer = layers[band];
                if (layer == null)
                {
                    CustomPlayerSkinMod.LogWarning($"  {LayerNames[band]}: レイヤー自体が無い");
                    continue;
                }

                SpriteRenderer? renderer = layer.sr;
                Material? material = renderer == null ? null : renderer.sharedMaterial;
                Sprite? sprite = renderer == null ? null : renderer.sprite;

                string useReplacement = material != null && material.HasProperty(UseReplacementProperty)
                    ? material.GetFloat(UseReplacementProperty).ToString("0.##")
                    : "なし";

                Texture? replacement = material != null && material.HasProperty(ReplacementProperty)
                    ? material.GetTexture(ReplacementProperty)
                    : null;

                CustomPlayerSkinMod.LogWarning(
                    $"  {LayerNames[band]}:"
                    + $" skin={Describe(layer.skin)}"
                    + $" renderer={(renderer == null ? "なし" : "あり")}"
                    + $" sprite={Describe(sprite == null ? null : sprite.texture)}"
                    + $" material={(material == null ? "なし" : material.shader == null ? "あり" : material.shader.name)}"
                    + $" mainTex={Describe(material == null ? null : material.mainTexture)}"
                    + $" replacementTex={Describe(replacement)}"
                    + $" useReplacement={useReplacement}");
            }
        }

        /// <summary>A texture's size, or a note that it is not there.</summary>
        private static string Describe(Texture? texture) =>
            texture == null ? "なし" : $"{texture.width}x{texture.height}";
    }
}
