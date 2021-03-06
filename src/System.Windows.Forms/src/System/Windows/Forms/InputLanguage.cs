﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace System.Windows.Forms
{
    /// <devdoc>
    /// Provides methods and fields to manage the input language.
    /// </devdoc>
    public sealed class InputLanguage
    {
        /// <devdoc>
        /// The HKL handle.
        /// </devdoc>
        private readonly IntPtr handle;

        internal InputLanguage(IntPtr handle)
        {
            this.handle = handle;
        }

        /// <devdoc>
        /// Returns the culture of the current input language.
        /// </devdoc>
        public CultureInfo Culture => new CultureInfo((int)handle & 0xFFFF);

        /// <devdoc>
        /// Gets or sets the input language for the current thread.
        /// </devdoc>
        public static InputLanguage CurrentInputLanguage
        {
            get
            {
                Application.OleRequired();
                // note we can obtain the KeyboardLayout for a given thread...
                return new InputLanguage(SafeNativeMethods.GetKeyboardLayout(0));
            }
            set
            {
                // OleInitialize needs to be called before we can call ActivateKeyboardLayout.
                Application.OleRequired();
                if (value == null)
                {
                    value = InputLanguage.DefaultInputLanguage;
                }
                IntPtr handleOld = SafeNativeMethods.ActivateKeyboardLayout(new HandleRef(value, value.handle), 0);
                if (handleOld == IntPtr.Zero)
                {
                    throw new ArgumentException(SR.ErrorBadInputLanguage, nameof(value));
                }
            }
        }

        /// <devdoc>
        /// Returns the default input language for the system.
        /// </devdoc>
        public static InputLanguage DefaultInputLanguage
        {
            get
            {
                IntPtr[] data = new IntPtr[1];
                UnsafeNativeMethods.SystemParametersInfo(NativeMethods.SPI_GETDEFAULTINPUTLANG, 0, data, 0);
                return new InputLanguage(data[0]);
            }
        }

        /// <include file='doc\InputLanguage.uex' path='docs/doc[@for="InputLanguage.Handle"]/*' />
        /// <devdoc>
        /// Returns the handle for the input language.
        /// </devdoc>
        public IntPtr Handle => handle;

        /// <devdoc>
        /// Returns a list of all installed input languages.
        /// </devdoc>
        public static InputLanguageCollection InstalledInputLanguages
        {
            get
            {
                int size = SafeNativeMethods.GetKeyboardLayoutList(0, null);

                IntPtr[] handles = new IntPtr[size];
                SafeNativeMethods.GetKeyboardLayoutList(size, handles);

                InputLanguage[] ils = new InputLanguage[size];
                for (int i = 0; i < size; i++)
                {
                    ils[i] = new InputLanguage(handles[i]);
                }

                return new InputLanguageCollection(ils);
            }
        }

        /// <devdoc>
        /// Returns the name of the current keyboard layout as it appears in the Windows
        /// Regional Settings on the computer.
        /// </devdoc>
        public string LayoutName
        {
            get
            {
                // There is no good way to do this in Windows.
                // GetKeyboardLayoutName does what we want, but only for the current input
                // language; setting and resetting the current input language would generate
                // spurious InputLanguageChanged events.

                /*
                            HKL is a 32 bit value. HIWORD is a Device Handle. LOWORD is Language ID.

                HKL
                +------------------------+-------------------------+
                |     Device Handle      |       Language ID       |
                +------------------------+-------------------------+
                31                     16 15                      0   bit


                Language ID
                +---------------------------+-----------------------+
                |     Sublanguage ID        | Primary Language ID   |
                +---------------------------+-----------------------+
                15                        10 9                     0   bit

                WORD LangId  = MAKELANGID(primary, sublang)
                BYTE primary = PRIMARYLANGID(LangId)
                BYTE sublang = PRIMARYLANGID(LangID)

                How Preload is interpreted: example US-Dvorak
                Look in HKEY_CURRENT_USER\Keyboard Layout\Preload
                Name="4"  (may vary)
                Value="d0000409"  -> Language ID = 0409
                Look in HKEY_CURRENT_USER\Keyboard Layout\Substitutes
                Name="d0000409"
                Value="00010409"
                Look in HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Keyboard Layouts\00010409
                "Layout File": name of keyboard layout DLL (KBDDV.DLL)
                "Layout Id": ID of this layout (0002)
                Windows will change the top nibble of layout ID to F, which makes F002.
                Combined with Language ID, the final HKL is F0020409.
                */

                string layoutName = null;

                IntPtr currentHandle = handle;
                int language = unchecked((int)(long)currentHandle) & 0xffff;
                int device = (unchecked((int)(long)currentHandle) >> 16) & 0x0fff;

                if (device == language || device == 0)
                {
                    // Default keyboard for language
                    string keyName = Convert.ToString(language, 16);
                    keyName = PadWithZeroes(keyName, 8);
                    RegistryKey key = Registry.LocalMachine.OpenSubKey("SYSTEM\\CurrentControlSet\\Control\\Keyboard Layouts\\" + keyName);

                    // Attempt to extract the localized keyboard layout name
                    // using the SHLoadIndirectString API...
                    layoutName = GetLocalizedKeyboardLayoutName(key.GetValue("Layout Display Name") as string);

                    // Default back to our legacy codepath and obtain the name
                    // directly through the registry value
                    if (layoutName == null)
                    {
                        layoutName = (string) key.GetValue("Layout Text");
                    }

                    key.Close();
                }
                else
                {
                    // Look for a substitution
                    RegistryKey substitutions = Registry.CurrentUser.OpenSubKey("Keyboard Layout\\Substitutes");
                    string[] encodings = null;
                    if (substitutions != null)
                    {
                        encodings = substitutions.GetValueNames();

                        foreach (string encoding in encodings)
                        {
                            int encodingValue = Convert.ToInt32(encoding, 16);
                            if (encodingValue == unchecked((int)(long)currentHandle) ||
                                (encodingValue & 0x0FFFFFFF) == (unchecked((int)(long)currentHandle) & 0x0FFFFFFF) ||
                                (encodingValue & 0xFFFF) == language)
                            {
                                currentHandle = (IntPtr)Convert.ToInt32((string)substitutions.GetValue(encoding), 16);
                                language = unchecked((int)(long)currentHandle) & 0xFFFF;
                                device = (unchecked((int)(long)currentHandle) >> 16) & 0xFFF;
                                break;
                            }
                        }

                        substitutions.Close();
                    }

                    RegistryKey layouts = Registry.LocalMachine.OpenSubKey("SYSTEM\\CurrentControlSet\\Control\\Keyboard Layouts");
                    if (layouts != null)
                    {
                        encodings = layouts.GetSubKeyNames();

                        // Check to see if the encoding directly matches the handle -- some do.
                        foreach (string encoding in encodings)
                        {
                            Debug.Assert(encoding.Length == 8, "unexpected key in registry: hklm\\SYSTEM\\CurrentControlSet\\Control\\Keyboard Layouts\\" + encoding);
                            if (currentHandle == (IntPtr)Convert.ToInt32(encoding, 16))
                            {
                                RegistryKey key = layouts.OpenSubKey(encoding);

                                // Attempt to extract the localized keyboard layout name
                                // using the SHLoadIndirectString API...
                                layoutName = GetLocalizedKeyboardLayoutName(key.GetValue("Layout Display Name") as string);

                                // Default back to our legacy codepath and obtain the name
                                // directly through the registry value
                                if (layoutName == null)
                                {
                                    layoutName = (string) key.GetValue("Layout Text");
                                }

                                key.Close();
                                break;
                            }
                        }
                    }

                    if (layoutName == null)
                    {
                        // No luck there.  Match the language first, then try to find a layout ID
                        foreach (string encoding in encodings)
                        {
                            Debug.Assert(encoding.Length == 8, "unexpected key in registry: hklm\\SYSTEM\\CurrentControlSet\\Control\\Keyboard Layouts\\" + encoding);
                            if (language == (0xffff & Convert.ToInt32(encoding.Substring(4,4), 16)))
                            {
                                RegistryKey key = layouts.OpenSubKey(encoding);
                                string codeValue = (string)key.GetValue("Layout Id");
                                if (codeValue != null)
                                {
                                    int value = Convert.ToInt32(codeValue, 16);
                                    if (value == device)
                                    {
                                        // Attempt to extract the localized keyboard layout name
                                        // using the SHLoadIndirectString API...
                                        layoutName = GetLocalizedKeyboardLayoutName(key.GetValue("Layout Display Name") as string);

                                        // Default back to our legacy codepath and obtain the name
                                        // directly through the registry value
                                        if (layoutName == null)
                                        {
                                            layoutName = (string)key.GetValue("Layout Text");
                                        }
                                    }
                                }
                                key.Close();
                                if (layoutName != null)
                                {
                                    break;
                                }
                            }
                        }
                    }

                    layouts.Close();
                }

                return layoutName ?? SR.UnknownInputLanguageLayout;
            }
        }

        /// <devdoc>
        /// Attempts to extract the localized keyboard layout name using the
        /// SHLoadIndirectString API (only on OSVersions >= 5).  Returning
        /// null from this method will force us to use the legacy codepath
        /// (pulling the text directly from the registry).
        /// </devdoc>
        private static string GetLocalizedKeyboardLayoutName(string layoutDisplayName)
        {
            if (layoutDisplayName != null)
            {
                StringBuilder sb = new StringBuilder(512);
                uint res = UnsafeNativeMethods.SHLoadIndirectString(layoutDisplayName, sb, (uint)sb.Capacity, IntPtr.Zero);
                if (res == NativeMethods.S_OK)
                {
                    return sb.ToString();
                }
            }

            return null;
        }

        /// <devdoc>
        /// Creates an InputLanguageChangedEventArgs given a windows message.
        /// </devdoc>
        internal static InputLanguageChangedEventArgs CreateInputLanguageChangedEventArgs(Message m)
        {
            return new InputLanguageChangedEventArgs(new InputLanguage(m.LParam), unchecked((byte)(long)m.WParam));
        }

        /// <devdoc>
        /// Creates an InputLanguageChangingEventArgs given a windows message.
        /// </devdoc>
        internal static InputLanguageChangingEventArgs CreateInputLanguageChangingEventArgs(Message m)
        {
            InputLanguage inputLanguage = new InputLanguage(m.LParam);

            // NOTE: by default we should allow any locale switch
            bool localeSupportedBySystem = m.WParam != IntPtr.Zero;
            return new InputLanguageChangingEventArgs(inputLanguage, localeSupportedBySystem);
        }

        /// <devdoc>
        /// Specifies whether two input languages are equal.
        /// </devdoc>
        public override bool Equals(object value)
        {
            return (value is InputLanguage other) && (this.handle == other.handle);
        }

        /// <devdoc>
        /// Returns the input language associated with the specified culture.
        /// </devdoc>
        public static InputLanguage FromCulture(CultureInfo culture)
        {
            if (culture == null)
            {
                throw new ArgumentNullException(nameof(culture));
            }

            // KeyboardLayoutId is the LCID for built-in cultures, but it
            // is the CU-preferred keyboard language for custom cultures.
            int lcid = culture.KeyboardLayoutId;

            foreach(InputLanguage lang in InstalledInputLanguages)
            {
                if ((unchecked((int)(long)lang.handle) & 0xFFFF) == lcid)
                {
                    return lang;
                }
            }

            return null;
        }

        /// <devdoc>
        /// Hash code for this input language.
        /// </devdoc>
        public override int GetHashCode() => unchecked((int)(long)handle);

        private static string PadWithZeroes(string input, int length)
        {
            return "0000000000000000".Substring(0, length - input.Length) + input;
        }
    }
}
