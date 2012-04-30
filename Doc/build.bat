rmdir /S /Q Api
mkdir Api
"C:\Program Files\doxygen\bin\doxygen.exe" Doxyfile
robocopy Api ..\..\RunLooper.Doc /E /TEE /R:0