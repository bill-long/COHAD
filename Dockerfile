FROM mcr.microsoft.com/dotnet/core/aspnet:2.1
ARG publish_output
RUN echo $publish_output
WORKDIR /app
COPY $publish_output/Web .
ENTRYPOINT [ "dotnet", "Web.dll" ]
